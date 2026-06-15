using Microsoft.Extensions.Logging.Abstractions;
using ShiftIt.Configuration;
using ShiftIt.Services;
using ShiftIt.Tests.TestHelpers;

namespace ShiftIt.Tests;

public sealed class ArchiveScannerTests
{
    private static ArchiveScanner CreateScanner(ArchiveOptions options)
    {
        var mover = new FileMover(NullLogger<FileMover>.Instance);
        return new ArchiveScanner(
            mover,
            new StaticOptionsMonitor<ArchiveOptions>(options),
            NullLogger<ArchiveScanner>.Instance);
    }

    private static ArchiveOptions OptionsFor(
        string hotRoot, string archiveRoot, int minAgeDays = 30, bool removeEmpty = true) => new()
    {
        MinAgeDays = minAgeDays,
        RemoveEmptyHotFolders = removeEmpty,
        VerifyWithHash = false,
        Pairs = [new TargetPair { Name = "Test", HotRoot = hotRoot, ArchiveRoot = archiveRoot }],
    };

    private static void Age(string path, int days) =>
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-days));

    [Fact]
    public async Task RunSweep_MovesAgedFiles_AndMirrorsFolderStructure()
    {
        using var temp = new TempDir();
        var hot = temp.Combine("hot");
        var archive = temp.Combine("archive");
        var aged = temp.WriteFile("hot/2024/Q1/old.txt", "old");
        Age(aged, 40);

        await CreateScanner(OptionsFor(hot, archive)).RunSweepAsync(CancellationToken.None);

        Assert.False(File.Exists(aged));
        Assert.True(File.Exists(Path.Combine(archive, "2024", "Q1", "old.txt")));
    }

    [Fact]
    public async Task RunSweep_LeavesRecentFilesInPlace()
    {
        using var temp = new TempDir();
        var hot = temp.Combine("hot");
        var archive = temp.Combine("archive");
        var fresh = temp.WriteFile("hot/recent/fresh.txt");   // default mtime = now

        await CreateScanner(OptionsFor(hot, archive)).RunSweepAsync(CancellationToken.None);

        Assert.True(File.Exists(fresh));
        Assert.False(Directory.Exists(archive)); // nothing archived, archive never created
    }

    [Fact]
    public async Task RunSweep_SkipsConflict_AndKeepsSource()
    {
        using var temp = new TempDir();
        var hot = temp.Combine("hot");
        var archive = temp.Combine("archive");
        var aged = temp.WriteFile("hot/dup.txt", "incoming");
        Age(aged, 40);
        temp.WriteFile("archive/dup.txt", "existing");

        await CreateScanner(OptionsFor(hot, archive)).RunSweepAsync(CancellationToken.None);

        Assert.True(File.Exists(aged));                                              // source kept
        Assert.Equal("existing", File.ReadAllText(Path.Combine(archive, "dup.txt"))); // not overwritten
    }

    [Fact]
    public async Task RunSweep_RemovesEmptyHotFolders_ButNotHotRoot()
    {
        using var temp = new TempDir();
        var hot = temp.Combine("hot");
        var archive = temp.Combine("archive");
        var aged = temp.WriteFile("hot/a/b/old.txt");
        Age(aged, 40);

        await CreateScanner(OptionsFor(hot, archive)).RunSweepAsync(CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(hot, "a")));  // emptied subtree pruned
        Assert.True(Directory.Exists(hot));                      // hot root preserved
    }

    [Fact]
    public async Task RunSweep_KeepsEmptyFolders_WhenRemoveDisabled()
    {
        using var temp = new TempDir();
        var hot = temp.Combine("hot");
        var archive = temp.Combine("archive");
        var aged = temp.WriteFile("hot/a/b/old.txt");
        Age(aged, 40);

        await CreateScanner(OptionsFor(hot, archive, removeEmpty: false)).RunSweepAsync(CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(hot, "a", "b"))); // folders retained
    }

    [Fact]
    public async Task RunSweep_DoesNotThrow_WhenHotRootMissing()
    {
        using var temp = new TempDir();
        var hot = temp.Combine("does-not-exist");
        var archive = temp.Combine("archive");

        var ex = await Record.ExceptionAsync(
            () => CreateScanner(OptionsFor(hot, archive)).RunSweepAsync(CancellationToken.None));

        Assert.Null(ex);
    }

    [Fact]
    public async Task RunSweep_HonorsMinAgeZero_MovesEverything()
    {
        using var temp = new TempDir();
        var hot = temp.Combine("hot");
        var archive = temp.Combine("archive");
        var justNow = temp.WriteFile("hot/now.txt"); // mtime = now

        await CreateScanner(OptionsFor(hot, archive, minAgeDays: 0)).RunSweepAsync(CancellationToken.None);

        Assert.False(File.Exists(justNow));
        Assert.True(File.Exists(Path.Combine(archive, "now.txt")));
    }
}
