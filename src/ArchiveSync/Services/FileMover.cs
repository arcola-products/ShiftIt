using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace ArchiveSync.Services;

/// <summary>
/// Moves a single file to its archive destination using a crash- and
/// cross-volume-safe sequence: copy to a temp file on the destination volume,
/// verify it, atomically rename it into place, then delete the source last.
/// The source is only ever removed after the archived copy is durable, so an
/// interrupted run can be safely re-run.
/// </summary>
public sealed class FileMover
{
    private const string TempSuffix = ".archtmp";
    private readonly ILogger<FileMover> _logger;

    public FileMover(ILogger<FileMover> logger) => _logger = logger;

    /// <param name="sourcePath">Absolute path to the file in the hot root.</param>
    /// <param name="destinationPath">Absolute target path under the archive root.</param>
    /// <param name="verifyWithHash">Compare SHA-256 in addition to byte length.</param>
    public async Task<MoveResult> MoveAsync(
        string sourcePath,
        string destinationPath,
        bool verifyWithHash,
        CancellationToken cancellationToken)
    {
        // 1. Conflict: never overwrite an existing archived file.
        if (File.Exists(destinationPath))
        {
            _logger.LogWarning(
                "Skipping {Source}: a file already exists at {Destination}.",
                sourcePath, destinationPath);
            return MoveResult.SkippedExists;
        }

        var destinationDir = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(destinationDir);

        var tempPath = destinationPath + TempSuffix;

        try
        {
            // 2. Copy to a temp file on the destination volume.
            File.Copy(sourcePath, tempPath, overwrite: true);

            // 3. Verify the copy matches the source.
            if (!await VerifyAsync(sourcePath, tempPath, verifyWithHash, cancellationToken))
            {
                _logger.LogError(
                    "Verification failed for {Source}; leaving source in place.", sourcePath);
                TryDelete(tempPath);
                return MoveResult.Failed;
            }

            // Preserve original timestamps on the archived copy.
            File.SetLastWriteTimeUtc(tempPath, File.GetLastWriteTimeUtc(sourcePath));
            File.SetCreationTimeUtc(tempPath, File.GetCreationTimeUtc(sourcePath));

            // 4. Atomic rename into place (same volume).
            File.Move(tempPath, destinationPath);

            // 5. Source removed last, only after the copy is durable.
            File.Delete(sourcePath);

            _logger.LogInformation("Archived {Source} -> {Destination}.", sourcePath, destinationPath);
            return MoveResult.Moved;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to archive {Source}; source left untouched.", sourcePath);
            TryDelete(tempPath);
            return MoveResult.Failed;
        }
    }

    private static async Task<bool> VerifyAsync(
        string sourcePath, string tempPath, bool verifyWithHash, CancellationToken ct)
    {
        var source = new FileInfo(sourcePath);
        var temp = new FileInfo(tempPath);

        if (source.Length != temp.Length)
        {
            return false;
        }

        if (!verifyWithHash)
        {
            return true;
        }

        var sourceHash = await ComputeHashAsync(sourcePath, ct);
        var tempHash = await ComputeHashAsync(tempPath, ct);
        return sourceHash.AsSpan().SequenceEqual(tempHash);
    }

    private static async Task<byte[]> ComputeHashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 20, useAsync: true);
        return await SHA256.HashDataAsync(stream, ct);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove temporary file {Temp}.", path);
        }
    }
}
