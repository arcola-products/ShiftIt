namespace ShiftIt.Services;

/// <summary>How the mover should react to a filesystem failure.</summary>
public enum FileErrorKind
{
    /// <summary>Unrecognised failure — fail just this file.</summary>
    Permanent,

    /// <summary>A momentary glitch worth retrying (file lock, brief network drop).</summary>
    Transient,

    /// <summary>The destination volume is out of space — abort the pair.</summary>
    DiskFull,

    /// <summary>Access was denied — skip this file.</summary>
    Permission,

    /// <summary>The drive or share is gone — abort the pair.</summary>
    Inaccessible,
}

/// <summary>
/// Classifies a filesystem exception into a <see cref="FileErrorKind"/> so the
/// mover can react with a single switch: retry transient glitches, abort a pair
/// when the destination is full or gone, and skip a file on a permission error.
/// </summary>
public static class FileErrors
{
    // Win32 error codes (low word of an IOException HResult).
    private const int ErrorNotReady = 21;          // device not ready
    private const int ErrorSharingViolation = 32;  // file in use
    private const int ErrorLockViolation = 33;
    private const int ErrorHandleDiskFull = 39;
    private const int ErrorBadNetpath = 53;        // network path not found
    private const int ErrorNetworkBusy = 54;
    private const int ErrorDevNotExist = 55;       // network resource gone
    private const int ErrorUnexpNetErr = 59;
    private const int ErrorNetnameDeleted = 64;    // connection dropped
    private const int ErrorDiskFull = 112;
    private const int ErrorIoDevice = 1117;        // I/O device error
    private const int ErrorNetworkUnreachable = 1231;

    public static FileErrorKind Classify(Exception ex) => ex switch
    {
        UnauthorizedAccessException => FileErrorKind.Permission,
        // Both derive from IOException, so they must be matched before it.
        DriveNotFoundException => FileErrorKind.Inaccessible,
        DirectoryNotFoundException => FileErrorKind.Inaccessible,
        IOException io => ClassifyIo(io.HResult & 0xFFFF),
        _ => FileErrorKind.Permanent,
    };

    private static FileErrorKind ClassifyIo(int win32Code) => win32Code switch
    {
        ErrorDiskFull or ErrorHandleDiskFull => FileErrorKind.DiskFull,
        ErrorNotReady or ErrorSharingViolation or ErrorLockViolation or
        ErrorBadNetpath or ErrorNetworkBusy or ErrorDevNotExist or
        ErrorUnexpNetErr or ErrorNetnameDeleted or ErrorIoDevice or
        ErrorNetworkUnreachable => FileErrorKind.Transient,
        _ => FileErrorKind.Permanent,
    };
}

/// <summary>
/// Thrown when a whole hot→archive pair cannot continue this sweep (destination
/// full or persistently inaccessible). The scanner catches it, logs once, and
/// moves on to the next pair instead of hammering every remaining file.
/// </summary>
public sealed class PairAbortedException(string reason, Exception? inner = null)
    : Exception(reason, inner);
