using Microsoft.Extensions.Logging.Abstractions;
using ShiftIt.Configuration;
using ShiftIt.Services;
using ShiftIt.Tests.TestHelpers;

namespace ShiftIt.Tests;

/// <summary>
/// End-to-end tests that drive the whole pipeline (scan → mirror → safe move →
/// prune) over a realistic, multi-nested tree of mixed file sizes, across local
/// and SMB destinations, sequential and parallel, with and without hashing.
/// </summary>
public sealed class SweepIntegrationTests
{
    private static ArchiveScanner CreateScanner(string hot, string archive, int parallelism, bool hash)
    {
        var options = new ArchiveOptions
        {
            MinAgeDays = 30,
            MaxRetries = 0,
            MaxParallelMoves = parallelism,
            VerifyWithHash = hash,
            RemoveEmptyHotFolders = true,
            Pairs = [new TargetPair { Name = "IT", HotRoot = hot, ArchiveRoot = archive }],
        };
        var monitor = new StaticOptionsMonitor<ArchiveOptions>(options);
        var mover = new FileMover(NullLogger<FileMover>.Instance, monitor);
        return new ArchiveScanner(mover, monitor, NullLogger<ArchiveScanner>.Instance);
    }

    private static string Local(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    [SkippableTheory]
    [InlineData(DestKind.Local, 1, false)] // sequential, local
    [InlineData(DestKind.Local, 8, false)] // parallel, local
    [InlineData(DestKind.Local, 4, true)]  // parallel, local, hash-verified
    [InlineData(DestKind.Smb, 8, false)]   // parallel, over SMB
    public async Task FullSweep_ArchivesNestedTree_PreservingStructureAndContent(
        DestKind dest, int parallelism, bool hash)
    {
        using var hot = new TempDir();
        using var archive = Targets.CreateArchive(dest);
        var manifest = TreeBuilder.BuildMixedTree(hot.Path);

        await CreateScanner(hot.Path, archive.Path, parallelism, hash).RunSweepAsync(CancellationToken.None);

        foreach (var file in manifest)
        {
            var hotPath = Local(hot.Path, file.RelativePath);
            var archivePath = Local(archive.Path, file.RelativePath);

            if (file.Aged)
            {
                Assert.False(File.Exists(hotPath), $"aged source should be gone: {file.RelativePath}");
                Assert.True(File.Exists(archivePath), $"aged file should be archived: {file.RelativePath}");
                Assert.Equal(file.Content, File.ReadAllBytes(archivePath)); // byte-exact mirror
            }
            else
            {
                Assert.True(File.Exists(hotPath), $"recent source should remain: {file.RelativePath}");
                Assert.False(File.Exists(archivePath), $"recent file should not be archived: {file.RelativePath}");
            }
        }

        var agedCount = manifest.Count(f => f.Aged);
        Assert.Equal(agedCount, Directory.GetFiles(archive.Path, "*", SearchOption.AllDirectories).Length);
        Assert.True(Directory.Exists(hot.Path)); // hot root preserved
    }

    [Fact]
    public async Task FullSweep_PrunesFullyEmptiedSubtree_KeepsFoldersWithRecentFiles()
    {
        using var hot = new TempDir();
        using var archive = new TempDir();
        var aged = hot.WriteFile("a/b/c/old.txt", "old");
        File.SetLastWriteTimeUtc(aged, DateTime.UtcNow.AddDays(-40));
        hot.WriteFile("keep/fresh.txt", "fresh"); // recent, stays

        await CreateScanner(hot.Path, archive.Path, parallelism: 4, hash: false)
            .RunSweepAsync(CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(hot.Path, "a")));   // emptied subtree pruned to the root
        Assert.True(Directory.Exists(Path.Combine(hot.Path, "keep"))); // folder with a recent file kept
        Assert.True(File.Exists(Path.Combine(archive.Path, "a", "b", "c", "old.txt")));
    }
}
