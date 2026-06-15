using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShiftIt.Configuration;

namespace ShiftIt.Services;

/// <summary>
/// Moves a single file to its archive destination using a crash- and
/// cross-volume-safe sequence: copy to a temp file on the destination volume,
/// verify it, atomically rename it into place, then delete the source last.
/// The source is only ever removed after the archived copy is durable, so an
/// interrupted run can be safely re-run.
///
/// Each filesystem step is retried on transient failures (sharing violations,
/// momentary network drops). A full or vanished destination raises
/// <see cref="PairAbortedException"/> so the scanner can stop the pair cleanly
/// instead of failing every remaining file. Per-file outcomes are logged at
/// Debug so they stay in the detailed file log and out of the Event Log.
/// </summary>
public sealed class FileMover : IFileMover
{
    private const string TempSuffix = ".archtmp";
    private readonly ILogger<FileMover> _logger;
    private readonly IOptionsMonitor<ArchiveOptions> _options;

    public FileMover(ILogger<FileMover> logger, IOptionsMonitor<ArchiveOptions> options)
    {
        _logger = logger;
        _options = options;
    }

    public async Task<MoveResult> MoveAsync(
        string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        // 1. Conflict: never overwrite an existing archived file.
        if (File.Exists(destinationPath))
        {
            _logger.LogDebug(
                "Skipping {Source}: a file already exists at {Destination}.",
                sourcePath, destinationPath);
            return MoveResult.SkippedExists;
        }

        var options = _options.CurrentValue;
        var maxRetries = options.MaxRetries;
        var baseDelay = TimeSpan.FromSeconds(options.RetryDelaySeconds);
        var destinationDir = Path.GetDirectoryName(destinationPath)!;
        var tempPath = destinationPath + TempSuffix;

        try
        {
            await Retry(() => { Directory.CreateDirectory(destinationDir); return Task.CompletedTask; },
                maxRetries, baseDelay, sourcePath, cancellationToken);

            // 2. Copy to a temp file on the destination volume. File.Copy is
            // OS-optimized and allocation-free; the whole step is retried as a
            // unit on transient failures.
            await Retry(() => { File.Copy(sourcePath, tempPath, overwrite: true); return Task.CompletedTask; },
                maxRetries, baseDelay, sourcePath, cancellationToken);

            // 3. Verify the copy matches the source.
            if (!await VerifyAsync(sourcePath, tempPath, options.VerifyWithHash, cancellationToken))
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
            await Retry(() => { File.Move(tempPath, destinationPath); return Task.CompletedTask; },
                maxRetries, baseDelay, sourcePath, cancellationToken);

            // 5. Source removed last, only after the copy is durable.
            try
            {
                await Retry(() => { File.Delete(sourcePath); return Task.CompletedTask; },
                    maxRetries, baseDelay, sourcePath, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The archived copy is safe; only the source cleanup failed. A
                // re-run will see the destination and skip, leaving the source.
                _logger.LogWarning(ex,
                    "Archived {Source} but could not remove the source; it will remain.", sourcePath);
                return MoveResult.Moved;
            }

            _logger.LogDebug("Archived {Source} -> {Destination}.", sourcePath, destinationPath);
            return MoveResult.Moved;
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            return HandleFailure(ex, sourcePath);
        }
    }

    /// <summary>Maps a failed move to either a per-file result or a pair abort.</summary>
    private MoveResult HandleFailure(Exception ex, string sourcePath)
    {
        switch (FileErrors.Classify(ex))
        {
            case FileErrorKind.DiskFull:
                throw new PairAbortedException("destination volume is full", ex);

            case FileErrorKind.Inaccessible:
                throw new PairAbortedException("archive destination is inaccessible", ex);

            case FileErrorKind.Transient:
                // Still failing after all retries — persistently unreachable.
                throw new PairAbortedException(
                    "source or destination is temporarily inaccessible (gave up after retries)", ex);

            case FileErrorKind.Permission:
                _logger.LogWarning(ex,
                    "Permission denied archiving {Source}; skipping this file.", sourcePath);
                return MoveResult.Failed;

            default:
                _logger.LogError(ex,
                    "Failed to archive {Source}; source left untouched.", sourcePath);
                return MoveResult.Failed;
        }
    }

    private Task Retry(
        Func<Task> action, int maxRetries, TimeSpan baseDelay, string source, CancellationToken ct) =>
        Resilience.RunWithRetryAsync(
            action, maxRetries, baseDelay,
            isTransient: ex => FileErrors.Classify(ex) == FileErrorKind.Transient,
            onRetry: (attempt, ex) => _logger.LogDebug(
                "Transient error on {Source} (attempt {Attempt}/{Max}): {Message}. Retrying...",
                source, attempt, maxRetries, ex.Message),
            ct);

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
            _logger.LogDebug(ex, "Could not remove temporary file {Temp}.", path);
        }
    }
}
