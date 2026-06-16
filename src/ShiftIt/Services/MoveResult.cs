namespace ShiftIt.Services;

/// <summary>Outcome of attempting to move a single file to the archive.</summary>
public enum MoveResult
{
    /// <summary>File was copied, verified, and the source removed.</summary>
    Moved,

    /// <summary>
    /// File was copied and verified, but the source was kept (copy-only mode).
    /// </summary>
    Copied,

    /// <summary>A file already existed at the destination; source left untouched.</summary>
    SkippedExists,

    /// <summary>An error occurred; source left untouched.</summary>
    Failed,
}
