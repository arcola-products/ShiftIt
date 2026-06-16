namespace ShiftIt.Tests.TestHelpers;

/// <summary>One generated file: its path relative to the hot root, bytes, and age.</summary>
public sealed record TreeFile(string RelativePath, byte[] Content, bool Aged);

/// <summary>
/// Builds a realistic, multi-nested hot tree mixing small and large files, with
/// a known set of aged (should-archive) and recent (should-stay) files, and
/// returns the manifest so a test can assert exact structure and content.
/// </summary>
public static class TreeBuilder
{
    private const int AgedDays = 40;

    // Relative folders at varying depths, including the hot root itself ("").
    private static readonly string[] Folders =
    [
        "",
        "reports",
        "reports/2023/q1",
        "reports/2023/q2",
        "logs/app",
        "logs/sys/deep/deeper",
        "media/images",
    ];

    /// <summary>
    /// Writes the tree under <paramref name="hotRoot"/> and back-dates the aged
    /// files. Roughly 4/5 of the files are aged; sizes range from 1 KB to 2 MB.
    /// </summary>
    public static IReadOnlyList<TreeFile> BuildMixedTree(string hotRoot, int seed = 1)
    {
        var rng = new Random(seed);
        var sizes = new[] { 1 << 10, 8 << 10, 64 << 10, 1 << 20, 2 << 20 }; // 1KB..2MB
        var files = new List<TreeFile>();
        var index = 0;

        foreach (var folder in Folders)
        {
            var perFolder = rng.Next(6, 11); // 6-10 files each
            for (var i = 0; i < perFolder; i++)
            {
                var size = sizes[rng.Next(sizes.Length)];
                // Bias toward small files; only occasionally pick the large sizes.
                if (size >= (1 << 20) && rng.Next(100) >= 20)
                {
                    size = 8 << 10;
                }

                var content = new byte[size];
                rng.NextBytes(content);

                var aged = rng.Next(5) != 0; // ~80% aged
                var relativePath = string.IsNullOrEmpty(folder)
                    ? $"file{index}.bin"
                    : $"{folder}/file{index}.bin";

                var full = Path.Combine(hotRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, content);
                if (aged)
                {
                    File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddDays(-AgedDays));
                }

                files.Add(new TreeFile(relativePath, content, aged));
                index++;
            }
        }

        return files;
    }
}
