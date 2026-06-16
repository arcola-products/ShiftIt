using Microsoft.Extensions.Logging.Abstractions;
using ShiftIt.Configuration;
using ShiftIt.Services;
using ShiftIt.Tests.TestHelpers;

namespace ShiftIt.Tests;

public sealed class ArchiveScannerTests
{
    private static ArchiveScanner CreateScanner(ArchiveOptions options, IFileMover? mover = null)
    {
        var monitor = new StaticOptionsMonitor<ArchiveOptions>(options);
        mover ??= new FileMover(NullLogger<FileMover>.Instance, monitor);
        return new ArchiveScanner(mover, monitor, NullLogger<ArchiveScanner>.Instance);
    }

    private static ArchiveOptions OptionsFor(
        string hotRoot, string archiveRoot, int minAgeDays = 30, bool removeEmpty = true) => new()
    {
        MinAgeDays = minAgeDays,
        RemoveEmptyHotFolders = removeEmpty,
        VerifyWithHash = false,
        MaxRetries = 0,
        Pairs = [new TargetPair { Name = "Test", HotRoot = hotRoot, ArchiveRoot = archiveRoot }],
    };

    private static void Age(string path, int days) =>
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-days));

    [SkippableTheory]
    [InlineData(DestKind.Local)] // local -> local
    [InlineData(DestKind.Smb)]   // local -> SMB share
    public async Task RunSweep_MovesAgedFiles_AndMirrorsFolderStructure(DestKind dest)
    {
        using var hotDir = new TempDir();
        using var archiveDir = Targets.CreateArchive(dest);
        var hot = hotDir.Path;
        var archive = archiveDir.Path;
        var aged = hotDir.WriteFile("2024/Q1/old.txt", "old");
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

    [Fact]
    public async Task RunSweep_CopyOnly_CopiesButKeepsSource_AndIsIdempotent()
    {
        using var hotDir = new TempDir();
        using var archiveDir = new TempDir();
        var aged = hotDir.WriteFile("a/b/old.txt", "payload");
        Age(aged, 40);

        var options = OptionsFor(hotDir.Path, archiveDir.Path);
        options.CopyOnly = true;

        await CreateScanner(options).RunSweepAsync(CancellationToken.None);

        var archived = Path.Combine(archiveDir.Path, "a", "b", "old.txt");
        Assert.True(File.Exists(aged));                                          // source kept
        Assert.True(File.Exists(archived));                                      // copy made
        Assert.Equal("payload", File.ReadAllText(archived));
        Assert.True(Directory.Exists(Path.Combine(hotDir.Path, "a", "b")));      // folder not pruned

        // Second sweep is idempotent: source stays, no duplicate archived file.
        await CreateScanner(options).RunSweepAsync(CancellationToken.None);
        Assert.True(File.Exists(aged));
        Assert.Single(Directory.GetFiles(archiveDir.Path, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task RunSweep_MovesAllFiles_AtAnyParallelism(int parallelism)
    {
        using var hotDir = new TempDir();
        using var archiveDir = new TempDir();
        const int count = 25;
        foreach (var i in Enumerable.Range(0, count))
        {
            Age(hotDir.WriteFile($"sub{i % 3}/file{i}.txt", $"data{i}"), 40);
        }

        var options = OptionsFor(hotDir.Path, archiveDir.Path);
        options.MaxParallelMoves = parallelism;

        await CreateScanner(options).RunSweepAsync(CancellationToken.None);

        // Every file archived exactly once, none lost or duplicated.
        Assert.Empty(Directory.GetFiles(hotDir.Path, "*", SearchOption.AllDirectories));
        Assert.Equal(count, Directory.GetFiles(archiveDir.Path, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task RunSweep_StopsPair_WhenMoverAborts()
    {
        using var temp = new TempDir();
        var hot = temp.Combine("hot");
        var archive = temp.Combine("archive");
        foreach (var i in Enumerable.Range(0, 5))
        {
            Age(temp.WriteFile($"hot/file{i}.txt"), 40);
        }

        // Mover aborts (e.g. destination full) on the very first file.
        var mover = new AbortingFileMover(abortOnCall: 1);

        // Should not throw, and should stop after the abort rather than calling
        // the mover for every remaining file.
        await CreateScanner(OptionsFor(hot, archive), mover).RunSweepAsync(CancellationToken.None);

        Assert.Equal(1, mover.Calls);                                  // stopped immediately
        Assert.Equal(5, Directory.GetFiles(hot).Length);              // all sources left intact
    }

    [SkippableFact]
    public async Task RunSweep_AbortsPair_AndKeepsSources_WhenArchiveDriveMissing()
    {
        var missingDrive = FirstUnusedDriveRoot();
        Skip.If(missingDrive is null, "No unused drive letter available to simulate a missing volume.");

        using var hotDir = new TempDir();
        var aged = hotDir.WriteFile("old.txt");
        Age(aged, 40);
        var archive = Path.Combine(missingDrive!, "shiftit-archive");

        // A real FileMover hitting a non-existent drive must not throw out of the sweep.
        var ex = await Record.ExceptionAsync(() =>
            CreateScanner(OptionsFor(hotDir.Path, archive)).RunSweepAsync(CancellationToken.None));

        Assert.Null(ex);
        Assert.True(File.Exists(aged));  // source preserved when the destination is unreachable
    }

    /// <summary>Finds a drive root (e.g. "Q:\") that does not exist, or null if none.</summary>
    private static string? FirstUnusedDriveRoot()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (var c = 'Z'; c >= 'D'; c--)
        {
            if (!used.Contains(c))
            {
                return $"{c}:\\";
            }
        }
        return null;
    }

    /// <summary>Throws <see cref="PairAbortedException"/> on the Nth call.</summary>
    private sealed class AbortingFileMover(int abortOnCall) : IFileMover
    {
        public int Calls { get; private set; }

        public Task<MoveResult> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct)
        {
            Calls++;
            if (Calls >= abortOnCall)
            {
                throw new PairAbortedException("destination volume is full");
            }
            return Task.FromResult(MoveResult.Moved);
        }
    }
}
