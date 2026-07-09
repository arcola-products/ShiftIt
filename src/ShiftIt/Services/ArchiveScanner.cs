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
    // Per-directory listing. Skips reparse points (junctions/symlinks) so a sweep
    // can never follow a link out of the hot tree, loop, or delete from a link's
    // target; skips hidden and system files (Thumbs.db, desktop.ini, ...); and
    // tolerates entries it cannot read. Recursion is manual so excluded folders
    // can be pruned without descending into them.
    private static readonly EnumerationOptions DirectoryEnumeration = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
        IgnoreInaccessible = true,
    };

    private readonly IFileMover _mover;
    private readonly IOptionsMonitor<ArchiveOptions> _options;
    private readonly ILogger<ArchiveScanner> _logger;
    private readonly IFailureTracker _failures;

    public ArchiveScanner(
        IFileMover mover,
        IOptionsMonitor<ArchiveOptions> options,
        ILogger<ArchiveScanner> logger,
        IFailureTracker? failures = null)
    {
        _mover = mover;
        _options = options;
        _logger = logger;
        _failures = failures ?? new FailureTracker(options);
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
        // Source folder dates captured before files move out, keyed by source dir.
        var sourceDirTimes = new ConcurrentDictionary<string, (DateTime Creation, DateTime LastWrite)>(
            StringComparer.OrdinalIgnoreCase);
        string? abortReason = null;

        // Walk the tree, pruning excluded folders; LastWriteTimeUtc comes from the
        // enumeration (no per-file stat). Skip leftover temp files too.
        var exclusions = new ExclusionMatcher(pair.ExcludedFolders);
        var eligible = EnumerateFiles(new DirectoryInfo(hotRoot), hotRoot, exclusions, sourceDirTimes)
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

                // Files that have failed too many times are skipped without a
                // mover call or a log line, so a stuck set is not retried — or
                // re-logged — on every sweep.
                if (_failures.IsQuarantined(source, fileInfo.LastWriteTimeUtc))
                {
                    stats.IncQuarantined();
                    return;
                }

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
                        _failures.Clear(source);
                        emptiedDirs.TryAdd(Path.GetDirectoryName(source)!, 0);
                        break;
                    case MoveResult.Copied:
                        stats.IncCopied(); // source kept, so the folder isn't emptied
                        _failures.Clear(source);
                        break;
                    case MoveResult.SkippedExists:
                        stats.IncSkipped();
                        _failures.Clear(source);
                        break;
                    default:
                        stats.IncFailed();
                        var count = _failures.RecordFailure(source, fileInfo.LastWriteTimeUtc);
                        if (count == options.MaxFileFailures)
                        {
                            // Log once, as it crosses into quarantine — not every sweep.
                            _logger.LogWarning(
                                "Quarantining {Source} after {Count} failed attempts; it will be skipped until it changes or the service restarts.",
                                source, count);
                        }
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

        // Mirror each source folder's original dates onto the archive folders.
        ApplyFolderTimestamps(ensuredDirs.Keys, archiveRoot, hotRoot, sourceDirTimes);

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

    /// <summary>
    /// Recursively yields files under <paramref name="dir"/>, pruning excluded
    /// folders, reparse points, and hidden/system entries, and skipping folders
    /// that can't be read rather than failing the sweep.
    /// </summary>
    private IEnumerable<FileInfo> EnumerateFiles(
        DirectoryInfo dir,
        string hotRoot,
        ExclusionMatcher exclusions,
        ConcurrentDictionary<string, (DateTime Creation, DateTime LastWrite)> sourceDirTimes)
    {
        FileInfo[]? files = null;
        DirectoryInfo[]? subdirs = null;
        try
        {
            files = dir.GetFiles("*", DirectoryEnumeration);
            subdirs = dir.GetDirectories("*", DirectoryEnumeration);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Skipping unreadable folder {Folder}.", dir.FullName);
        }

        if (files is null || subdirs is null)
        {
            yield break;
        }

        // Snapshot this folder's dates now — before any of its files move out and
        // change its last-write time — so they can be mirrored to the archive.
        sourceDirTimes.TryAdd(dir.FullName, (dir.CreationTimeUtc, dir.LastWriteTimeUtc));

        foreach (var file in files)
        {
            yield return file;
        }

        foreach (var sub in subdirs)
        {
            if (exclusions.IsExcluded(sub.FullName, hotRoot))
            {
                _logger.LogDebug("Excluding folder {Folder}.", sub.FullName);
                continue;
            }

            foreach (var file in EnumerateFiles(sub, hotRoot, exclusions, sourceDirTimes))
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// Stamps each created archive folder (and its parents up to the archive root)
    /// with the original dates captured from the matching source folder.
    /// </summary>
    private void ApplyFolderTimestamps(
        IEnumerable<string> archiveDirs,
        string archiveRoot,
        string hotRoot,
        IReadOnlyDictionary<string, (DateTime Creation, DateTime LastWrite)> sourceDirTimes)
    {
        var stamped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in archiveDirs)
        {
            var dir = start;
            while (!string.Equals(dir, archiveRoot, StringComparison.OrdinalIgnoreCase)
                   && dir.StartsWith(archiveRoot, StringComparison.OrdinalIgnoreCase)
                   && stamped.Add(dir))
            {
                var relativePath = Path.GetRelativePath(archiveRoot, dir);
                var sourceDir = Path.Combine(hotRoot, relativePath);
                if (sourceDirTimes.TryGetValue(sourceDir, out var times) && Directory.Exists(dir))
                {
                    try
                    {
                        Directory.SetCreationTimeUtc(dir, times.Creation);
                        Directory.SetLastWriteTimeUtc(dir, times.LastWrite);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not set dates on archive folder {Folder}.", dir);
                    }
                }

                dir = Path.GetDirectoryName(dir)!;
            }
        }
    }

    private void LogSummary(string pair, SweepStats stats, string? abortReason)
    {
        if (abortReason is not null)
        {
            _logger.LogWarning(
                "[{Pair}] Sweep aborted ({Reason}): {Moved} moved, {Copied} copied, {Skipped} skipped, {Failed} failed, {Quarantined} quarantined before stopping.",
                pair, abortReason, stats.Moved, stats.Copied, stats.Skipped, stats.Failed, stats.Quarantined);
        }
        else if (stats.Failed > 0)
        {
            _logger.LogWarning(
                "[{Pair}] Sweep completed with errors: {Moved} moved, {Copied} copied, {Skipped} skipped, {Failed} failed, {Quarantined} quarantined.",
                pair, stats.Moved, stats.Copied, stats.Skipped, stats.Failed, stats.Quarantined);
        }
        else
        {
            // No new failures this sweep. Reported at Information even when files
            // remain quarantined, so a steady stuck set doesn't warn every sweep.
            _logger.LogInformation(
                "[{Pair}] Sweep complete: {Moved} moved, {Copied} copied, {Skipped} skipped, {Failed} failed, {Quarantined} quarantined.",
                pair, stats.Moved, stats.Copied, stats.Skipped, stats.Failed, stats.Quarantined);
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

    /// <summary>Matches folders to skip, by bare name (anywhere) or relative path.</summary>
    private sealed class ExclusionMatcher
    {
        private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _relativePaths = new(StringComparer.OrdinalIgnoreCase);

        public ExclusionMatcher(IEnumerable<string>? excluded)
        {
            foreach (var entry in excluded ?? [])
            {
                var normalized = entry.Trim()
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Trim(Path.DirectorySeparatorChar);
                if (normalized.Length == 0)
                {
                    continue;
                }

                if (normalized.Contains(Path.DirectorySeparatorChar))
                {
                    _relativePaths.Add(normalized);
                }
                else
                {
                    _names.Add(normalized);
                }
            }
        }

        public bool IsExcluded(string directoryFullPath, string hotRoot)
        {
            if (_names.Contains(Path.GetFileName(directoryFullPath)))
            {
                return true;
            }

            return _relativePaths.Count > 0
                && _relativePaths.Contains(Path.GetRelativePath(hotRoot, directoryFullPath));
        }
    }

    /// <summary>Thread-safe move counters for a single pair's sweep.</summary>
    private sealed class SweepStats
    {
        private int _moved;
        private int _copied;
        private int _skipped;
        private int _failed;
        private int _quarantined;

        public int Moved => Volatile.Read(ref _moved);
        public int Copied => Volatile.Read(ref _copied);
        public int Skipped => Volatile.Read(ref _skipped);
        public int Failed => Volatile.Read(ref _failed);
        public int Quarantined => Volatile.Read(ref _quarantined);

        public void IncMoved() => Interlocked.Increment(ref _moved);
        public void IncCopied() => Interlocked.Increment(ref _copied);
        public void IncSkipped() => Interlocked.Increment(ref _skipped);
        public void IncFailed() => Interlocked.Increment(ref _failed);
        public void IncQuarantined() => Interlocked.Increment(ref _quarantined);
    }
}
