namespace ShiftIt.Services;

/// <summary>
/// Remembers which source files keep failing to archive, so the scanner can stop
/// retrying — and re-logging — the same failures on every sweep. Without this, a
/// set of permanently failing files (e.g. permission-denied) is reprocessed and
/// re-logged each sweep, growing the logs without bound.
/// </summary>
public interface IFailureTracker
{
    /// <summary>
    /// True when this file has already failed enough times to be skipped this
    /// sweep. A change to the file (different last-write time) clears the record
    /// and gives it a fresh start.
    /// </summary>
    bool IsQuarantined(string path, DateTime lastWriteUtc);

    /// <summary>Records a failed attempt and returns the new consecutive-failure count.</summary>
    int RecordFailure(string path, DateTime lastWriteUtc);

    /// <summary>Clears any record for a file that has now succeeded (or is gone).</summary>
    void Clear(string path);
}
