using System.Collections.Concurrent;
using ShiftIt.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ShiftIt.Services;

/// <summary>
/// Orchestrates a sweep: for each configured pair, finds files in the hot root
/// that are older than the age threshold, mirrors their relative path under the
/// archive root, delegates the transfer to <see cref="IFileMover"/>, and finally
/// prunes folders left empty by the moves.
///
/// File ages come straight from the directory enumeration (no extra stat per
/// file); each destination directory is created at most once per sweep; and
/// moves can run concurrently (<see cref="ArchiveOptions.MaxParallelMoves"/>) to
/// hide per-file latency on high-latency targets such as SMB shares.
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
        var stats = new SweepStats();
        var ensuredDirs = new ConcurrentDictionary<string, Lazy<Task>>(StringComparer.OrdinalIgnoreCase);
        var emptiedDirs = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        string? abortReason = null;

        // LastWriteTimeUtc comes from the enumeration data — no per-file stat.
        // Skip leftover temp files from a previous interrupted run.
        var eligible = new DirectoryInfo(hotRoot)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => !f.Name.EndsWith(".archtmp", StringComparison.OrdinalIgnoreCase)
                        && f.LastWriteTimeUtc <= cutoffUtc);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, options.MaxParallelMoves),
            CancellationToken = cancellationToken,
        };

        try
        {
            await Parallel.ForEachAsync(eligible, parallelOptions, async (fileInfo, token) =>
            {
                var source = fileInfo.FullName;
                var relativePath = Path.GetRelativePath(hotRoot, source);
                var destination = Path.Combine(archiveRoot, relativePath);
                var destinationDir = Path.GetDirectoryName(destination)!;

                // Create each archive sub-directory at most once per sweep; all
                // files targeting it await the same creation task (so concurrent
                // movers never copy into a not-yet-created directory).
                var ensure = ensuredDirs.GetOrAdd(
                    destinationDir,
                    d => new Lazy<Task>(() => EnsureDirectoryAsync(d, options, cancellationToken)));
                await ensure.Value;

                var result = await _mover.MoveAsync(source, destination, token);
                switch (result)
                {
                    case MoveResult.Moved:
                        stats.IncMoved();
                        emptiedDirs.TryAdd(Path.GetDirectoryName(source)!, 0);
                        break;
                    case MoveResult.Copied:
                        stats.IncCopied(); // source kept, so the folder isn't emptied
                        break;
                    case MoveResult.SkippedExists:
                        stats.IncSkipped();
                        break;
                    default:
                        stats.IncFailed();
                        break;
                }
            });
        }
        catch (PairAbortedException ex)
        {
            abortReason = ex.Message;
        }
        catch (AggregateException ex) when (ex.InnerExceptions.OfType<PairAbortedException>().Any())
        {
            abortReason = ex.InnerExceptions.OfType<PairAbortedException>().First().Message;
        }

        if (options.RemoveEmptyHotFolders)
        {
            PruneEmptyDirectories(emptiedDirs.Keys, hotRoot);
        }

        LogSummary(pair.Name, stats, abortReason);
    }

    /// <summary>
    /// Ensures an archive sub-directory exists, retrying transient failures. A
    /// hard failure (full, gone, denied) aborts the whole pair, since it affects
    /// every file destined for that directory.
    /// </summary>
    private async Task EnsureDirectoryAsync(string dir, ArchiveOptions options, CancellationToken ct)
    {
        try
        {
            await Resilience.RunWithRetryAsync(
                () => { Directory.CreateDirectory(dir); return Task.CompletedTask; },
                options.MaxRetries,
                TimeSpan.FromSeconds(options.RetryDelaySeconds),
                isTransient: ex => FileErrors.Classify(ex) == FileErrorKind.Transient,
                onRetry: (attempt, ex) => _logger.LogDebug(
                    "Transient error creating {Dir} (attempt {Attempt}): {Message}. Retrying...",
                    dir, attempt, ex.Message),
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var reason = FileErrors.Classify(ex) switch
            {
                FileErrorKind.DiskFull => "destination volume is full",
                FileErrorKind.Inaccessible => "archive destination is inaccessible",
                FileErrorKind.Permission => "permission denied creating the archive directory",
                FileErrorKind.Transient => "archive destination is temporarily inaccessible (gave up after retries)",
                _ => "could not create the archive directory",
            };
            throw new PairAbortedException(reason, ex);
        }
    }

    private void LogSummary(string pair, SweepStats stats, string? abortReason)
    {
        if (abortReason is not null)
        {
            _logger.LogWarning(
                "[{Pair}] Sweep aborted ({Reason}): {Moved} moved, {Copied} copied, {Skipped} skipped, {Failed} failed before stopping.",
                pair, abortReason, stats.Moved, stats.Copied, stats.Skipped, stats.Failed);
        }
        else if (stats.Failed > 0)
        {
            _logger.LogWarning(
                "[{Pair}] Sweep completed with errors: {Moved} moved, {Copied} copied, {Skipped} skipped, {Failed} failed.",
                pair, stats.Moved, stats.Copied, stats.Skipped, stats.Failed);
        }
        else
        {
            _logger.LogInformation(
                "[{Pair}] Sweep complete: {Moved} moved, {Copied} copied, {Skipped} skipped, {Failed} failed.",
                pair, stats.Moved, stats.Copied, stats.Skipped, stats.Failed);
        }
    }

    /// <summary>
    /// Walks up from each directory a file was moved out of, deleting it (and any
    /// parents it leaves empty) up to — but never including — the hot root. Only
    /// directories touched this sweep are visited, so the whole tree is not
    /// re-walked.
    /// </summary>
    private void PruneEmptyDirectories(IEnumerable<string> startDirs, string hotRoot)
    {
        foreach (var start in startDirs)
        {
            var dir = start;
            while (!string.Equals(dir, hotRoot, StringComparison.OrdinalIgnoreCase)
                   && dir.StartsWith(hotRoot, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        break; // already gone, or not empty
                    }

                    Directory.Delete(dir);
                    _logger.LogDebug("Removed empty folder {Folder}.", dir);
                    dir = Path.GetDirectoryName(dir)!;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not remove folder {Folder}.", dir);
                    break;
                }
            }
        }
    }

    /// <summary>Thread-safe move counters for a single pair's sweep.</summary>
    private sealed class SweepStats
    {
        private int _moved;
        private int _copied;
        private int _skipped;
        private int _failed;

        public int Moved => Volatile.Read(ref _moved);
        public int Copied => Volatile.Read(ref _copied);
        public int Skipped => Volatile.Read(ref _skipped);
        public int Failed => Volatile.Read(ref _failed);

        public void IncMoved() => Interlocked.Increment(ref _moved);
        public void IncCopied() => Interlocked.Increment(ref _copied);
        public void IncSkipped() => Interlocked.Increment(ref _skipped);
        public void IncFailed() => Interlocked.Increment(ref _failed);
    }
}
