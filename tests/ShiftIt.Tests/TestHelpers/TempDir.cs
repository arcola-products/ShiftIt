namespace ShiftIt.Tests.TestHelpers;

/// <summary>
/// Creates a unique temporary directory and recursively deletes it on dispose,
/// so filesystem-backed tests stay isolated and self-cleaning.
/// </summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "shiftit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>Resolves a path relative to this temp directory.</summary>
    public string Combine(params string[] parts) =>
        System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    /// <summary>Creates a file (and any parent directories) with the given content.</summary>
    public string WriteFile(string relativePath, string content = "data")
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; ignore locked-file races on teardown.
        }
    }
}
