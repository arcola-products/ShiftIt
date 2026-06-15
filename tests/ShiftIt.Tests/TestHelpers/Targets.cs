namespace ShiftIt.Tests.TestHelpers;

/// <summary>Where a test's archive root lives.</summary>
public enum DestKind
{
    /// <summary>A local temp directory.</summary>
    Local,

    /// <summary>A folder on the SMB share, exercising cross-machine moves.</summary>
    Smb,
}

/// <summary>The SMB share used for network-path tests, probed once for availability.</summary>
public static class SmbTarget
{
    public const string Root = @"S:\The Archive\temp";

    public static bool Available { get; } = Probe();

    private static bool Probe()
    {
        try
        {
            Directory.CreateDirectory(Root);
            var probe = Path.Combine(Root, "probe-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Builds archive roots for tests, skipping SMB cases when the share is offline.</summary>
public static class Targets
{
    public static TempDir CreateArchive(DestKind kind)
    {
        if (kind == DestKind.Smb)
        {
            Skip.IfNot(SmbTarget.Available, $"SMB share '{SmbTarget.Root}' is not available.");
            return new TempDir(SmbTarget.Root);
        }

        return new TempDir();
    }
}
