using System.ComponentModel.DataAnnotations;

namespace ArchiveSync.Configuration;

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
