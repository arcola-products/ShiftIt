using System.ComponentModel.DataAnnotations;

namespace ShiftIt.Configuration;

/// <summary>
/// Root options bound from the "Archive" section of appsettings.json.
/// </summary>
public sealed class ArchiveOptions
{
    public const string SectionName = "Archive";

    /// <summary>How often a sweep runs.</summary>
    [Range(1, int.MaxValue)]
    public int ScanIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// A file is eligible for archiving once its LastWriteTimeUtc is at least
    /// this many days in the past. 0 means "archive everything currently present".
    /// </summary>
    [Range(0, int.MaxValue)]
    public int MinAgeDays { get; set; } = 30;

    /// <summary>Delete folders left empty under a hot root after files move out.</summary>
    public bool RemoveEmptyHotFolders { get; set; } = true;

    /// <summary>
    /// When true, the copied file is verified against the source with a SHA-256
    /// hash. When false, only the byte length is compared (faster).
    /// </summary>
    public bool VerifyWithHash { get; set; } = false;

    /// <summary>
    /// Number of times a file operation is retried after a transient failure
    /// (e.g. a sharing violation or a momentary network drop) before giving up.
    /// </summary>
    [Range(0, 20)]
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Base delay between transient-failure retries, in seconds. The wait grows
    /// exponentially per attempt (delay, 2x, 4x, ...).
    /// </summary>
    [Range(0, 600)]
    public double RetryDelaySeconds { get; set; } = 2;

    /// <summary>
    /// How many files to move concurrently within a pair. 1 is sequential.
    /// Higher values hide per-file latency on high-latency targets such as SMB
    /// shares, at the cost of more simultaneous I/O.
    /// </summary>
    [Range(1, 256)]
    public int MaxParallelMoves { get; set; } = 1;

    /// <summary>
    /// Directory for the detailed rolling file log. Relative paths are resolved
    /// against the application's base directory. A daily log file is written here.
    /// </summary>
    public string LogDirectory { get; set; } = "logs";

    /// <summary>
    /// How many days of rolling file logs to keep. Older daily log files are
    /// deleted automatically.
    /// </summary>
    [Range(1, 3650)]
    public int LogRetentionDays { get; set; } = 14;

    /// <summary>The hot-to-archive root mappings to process each sweep.</summary>
    [Required, MinLength(1)]
    public List<TargetPair> Pairs { get; set; } = new();
}

/// <summary>
/// A single hot root mapped to its archive root. The folder structure beneath
/// <see cref="HotRoot"/> is mirrored beneath <see cref="ArchiveRoot"/>.
/// </summary>
public sealed class TargetPair
{
    /// <summary>Friendly name used in logs.</summary>
    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>Source root that is kept clean.</summary>
    [Required]
    public string HotRoot { get; set; } = string.Empty;

    /// <summary>Destination root that receives aged files.</summary>
    [Required]
    public string ArchiveRoot { get; set; } = string.Empty;
}
