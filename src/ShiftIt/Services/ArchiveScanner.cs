using ShiftIt.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ShiftIt.Services;

/// <summary>
/// Orchestrates a sweep: for each configured pair, finds files in the hot root
/// that are older than the age threshold, mirrors their relative path under the
/// archive root, delegates the transfer to <see cref="IFileMover"/>, and finally
/// prunes folders left empty in the hot tree.
///
/// Per-file detail is left to the mover (Debug, file log only). The scanner
/// emits a single aggregated summary per pair — the level rises to Warning when
/// anything failed or the pair was aborted — so the Event Log stays readable.
/// </summary>
public sealed class ArchiveScanner : IArchiveScanner
{
    private readonly IFileMover _mover;
    private readonly IOptionsMonitor<ArchiveOptions> _options;
    private readonly ILogger<ArchiveScanner> _logger;

    public ArchiveScanner(
        IFileMover mover,
        IOptionsMonitor<ArchiveOptions> options,
        ILogger<ArchiveScanner> logger)
    {
        _mover = mover;
        _options = options;
        _logger = logger;
    }

    public async Task RunSweepAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var cutoffUtc = DateTime.UtcNow.AddDays(-options.MinAgeDays);

        foreach (var pair in options.Pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessPairAsync(pair, cutoffUtc, options, cancellationToken);
        }
    }

    private async Task ProcessPairAsync(
        TargetPair pair,
        DateTime cutoffUtc,
        ArchiveOptions options,
        CancellationToken cancellationToken)
    {
        var hotRoot = Path.GetFullPath(pair.HotRoot);

        if (!Directory.Exists(hotRoot))
        {
            _logger.LogWarning(
                "[{Pair}] Hot root does not exist: {HotRoot}. Skipping.", pair.Name, hotRoot);
            return;
        }

        var archiveRoot = Path.GetFullPath(pair.ArchiveRoot);
        int moved = 0, skipped = 0, failed = 0;
        string? abortReason = null;

        try
        {
            foreach (var file in Directory.EnumerateFiles(hotRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Skip our own in-flight temp files from a previous interrupted run.
                if (file.EndsWith(".archtmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                DateTime lastWriteUtc;
                try
                {
                    lastWriteUtc = File.GetLastWriteTimeUtc(file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[{Pair}] Could not read timestamp for {File}.", pair.Name, file);
                    failed++;
                    continue;
                }

                if (lastWriteUtc > cutoffUtc)
                {
                    continue; // Not old enough yet.
                }

                var relativePath = Path.GetRelativePath(hotRoot, file);
                var destination = Path.Combine(archiveRoot, relativePath);

                var result = await _mover.MoveAsync(file, destination, cancellationToken);
                switch (result)
                {
                    case MoveResult.Moved: moved++; break;
                    case MoveResult.SkippedExists: skipped++; break;
                    default: failed++; break;
                }
            }
        }
        catch (PairAbortedException ex)
        {
            // Destination full or gone: stop this pair, keep the reason for the summary.
            abortReason = ex.Message;
        }

        if (options.RemoveEmptyHotFolders)
        {
            RemoveEmptyDirectories(hotRoot, hotRoot);
        }

        LogSummary(pair.Name, moved, skipped, failed, abortReason);
    }

    private void LogSummary(string pair, int moved, int skipped, int failed, string? abortReason)
    {
        if (abortReason is not null)
        {
            _logger.LogWarning(
                "[{Pair}] Sweep aborted ({Reason}): {Moved} moved, {Skipped} skipped, {Failed} failed before stopping.",
                pair, abortReason, moved, skipped, failed);
        }
        else if (failed > 0)
        {
            _logger.LogWarning(
                "[{Pair}] Sweep completed with errors: {Moved} moved, {Skipped} skipped, {Failed} failed.",
                pair, moved, skipped, failed);
        }
        else
        {
            _logger.LogInformation(
                "[{Pair}] Sweep complete: {Moved} moved, {Skipped} skipped, {Failed} failed.",
                pair, moved, skipped, failed);
        }
    }

    /// <summary>
    /// Depth-first removal of empty directories beneath (but never including)
    /// <paramref name="root"/>.
    /// </summary>
    private void RemoveEmptyDirectories(string current, string root)
    {
        foreach (var dir in Directory.EnumerateDirectories(current))
        {
            RemoveEmptyDirectories(dir, root);
        }

        if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
        {
            return; // Never delete the hot root itself.
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(current).Any())
            {
                Directory.Delete(current);
                _logger.LogDebug("Removed empty folder {Folder}.", current);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove folder {Folder}.", current);
        }
    }
}
