using System.Collections.Concurrent;
using ShiftIt.Configuration;
using Microsoft.Extensions.Options;

namespace ShiftIt.Services;

/// <summary>
/// In-memory <see cref="IFailureTracker"/>, keyed by source path. Holds an entry
/// only for files that are currently failing (cleared on success), so its size is
/// bounded by the number of genuinely stuck files. State is not persisted: a
/// service restart re-attempts each file once more, then quarantines it again.
/// </summary>
public sealed class FailureTracker : IFailureTracker
{
    private readonly record struct State(int Failures, long LastWriteTicks);

    private readonly ConcurrentDictionary<string, State> _state =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IOptionsMonitor<ArchiveOptions> _options;

    public FailureTracker(IOptionsMonitor<ArchiveOptions> options) => _options = options;

    public bool IsQuarantined(string path, DateTime lastWriteUtc)
    {
        var max = _options.CurrentValue.MaxFileFailures;
        if (max <= 0)
        {
            return false; // quarantine disabled
        }

        if (!_state.TryGetValue(path, out var state))
        {
            return false;
        }

        // A modified file is treated as new work: forget the old failures.
        if (state.LastWriteTicks != lastWriteUtc.Ticks)
        {
            _state.TryRemove(path, out _);
            return false;
        }

        return state.Failures >= max;
    }

    public int RecordFailure(string path, DateTime lastWriteUtc)
    {
        var ticks = lastWriteUtc.Ticks;
        var updated = _state.AddOrUpdate(
            path,
            _ => new State(1, ticks),
            (_, existing) => existing.LastWriteTicks == ticks
                ? existing with { Failures = existing.Failures + 1 }
                : new State(1, ticks)); // file changed since last failure: reset
        return updated.Failures;
    }

    public void Clear(string path) => _state.TryRemove(path, out _);
}
