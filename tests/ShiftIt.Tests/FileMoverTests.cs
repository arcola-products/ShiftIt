using Microsoft.Extensions.Logging.Abstractions;
using ShiftIt.Configuration;
using ShiftIt.Services;
using ShiftIt.Tests.TestHelpers;

namespace ShiftIt.Tests;

public sealed class FileMoverTests
{
    // MaxRetries: 0 keeps tests fast (no real retry delays on unexpected errors).
    private static FileMover CreateMover(bool verifyWithHash = false)
    {
        var options = new ArchiveOptions { VerifyWithHash = verifyWithHash, MaxRetries = 0 };
        return new FileMover(NullLogger<FileMover>.Instance, new StaticOptionsMonitor<ArchiveOptions>(options));
    }

    // The scanner owns directory creation, so the mover's caller ensures the
    // destination directory exists before invoking it.
    private static string EnsureDest(string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        return destination;
    }

    [SkippableTheory]
    [InlineData(DestKind.Local)] // local -> local
    [InlineData(DestKind.Smb)]   // local -> SMB share
    public async Task MoveAsync_MovesFileToMirroredPath(DestKind dest)
    {
        using var hot = new TempDir();                  // source is always local
        using var archive = Targets.CreateArchive(dest); // local or SMB
        var source = hot.WriteFile("file.txt", "hello");
        var destination = EnsureDest(archive.Combine("nested", "file.txt"));

        var result = await CreateMover().MoveAsync(source, destination, CancellationToken.None);

        Assert.Equal(MoveResult.Moved, result);
        Assert.False(File.Exists(source));            // source removed last
        Assert.True(File.Exists(destination));
        Assert.Equal("hello", File.ReadAllText(destination));
    }

    [Theory]
    [InlineData(false)] // File.Copy path
    [InlineData(true)]  // streamed hash path
    public async Task MoveAsync_PreservesLastWriteTime(bool verifyWithHash)
    {
        using var temp = new TempDir();
        var source = temp.WriteFile("hot/file.txt");
        var stamp = DateTime.UtcNow.AddDays(-100);
        File.SetLastWriteTimeUtc(source, stamp);
        var destination = EnsureDest(temp.Combine("archive", "file.txt"));

        await CreateMover(verifyWithHash).MoveAsync(source, destination, CancellationToken.None);

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(destination), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task MoveAsync_SkipsAndKeepsSource_WhenDestinationExists()
    {
        using var temp = new TempDir();
        var source = temp.WriteFile("hot/file.txt", "incoming");
        var destination = temp.WriteFile("archive/file.txt", "existing");

        var result = await CreateMover().MoveAsync(source, destination, CancellationToken.None);

        Assert.Equal(MoveResult.SkippedExists, result);
        Assert.True(File.Exists(source));                            // source untouched
        Assert.Equal("existing", File.ReadAllText(destination));     // destination not overwritten
    }

    [SkippableTheory]
    [InlineData(DestKind.Local)]
    [InlineData(DestKind.Smb)]
    public async Task MoveAsync_WithHashVerification_MovesAndMatchesContent(DestKind dest)
    {
        using var hot = new TempDir();
        using var archive = Targets.CreateArchive(dest);
        var content = new string('x', 50_000);
        var source = hot.WriteFile("big.bin", content);
        var destination = archive.Combine("big.bin");

        var result = await CreateMover(verifyWithHash: true).MoveAsync(source, destination, CancellationToken.None);

        Assert.Equal(MoveResult.Moved, result);
        Assert.Equal(content, File.ReadAllText(destination));
    }

    [Fact]
    public async Task MoveAsync_LeavesNoTempFile_OnSuccess()
    {
        using var temp = new TempDir();
        var source = temp.WriteFile("hot/file.txt");
        var destination = EnsureDest(temp.Combine("archive", "file.txt"));

        await CreateMover().MoveAsync(source, destination, CancellationToken.None);

        var leftovers = Directory.GetFiles(temp.Path, "*.archtmp", SearchOption.AllDirectories);
        Assert.Empty(leftovers);
    }

    [Fact]
    public async Task MoveAsync_Fails_WhenSourceMissing()
    {
        using var hot = new TempDir();
        using var archive = new TempDir();
        var missing = hot.Combine("ghost.txt");        // never created
        var destination = archive.Combine("ghost.txt");

        var result = await CreateMover().MoveAsync(missing, destination, CancellationToken.None);

        Assert.Equal(MoveResult.Failed, result);
        Assert.False(File.Exists(destination));
    }

    [SkippableFact]
    public async Task MoveAsync_AbortsPair_WhenSourceLockedExclusively()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "Exclusive file-share locking (FileShare.None blocking reads) is Windows-specific.");

        using var hot = new TempDir();
        using var archive = new TempDir();
        var source = hot.WriteFile("locked.txt", "data");
        var destination = archive.Combine("locked.txt");

        // Hold an exclusive lock so the mover cannot read the source. With no
        // retries this surfaces immediately as a persistent transient -> abort.
        using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<PairAbortedException>(() =>
                CreateMover().MoveAsync(source, destination, CancellationToken.None));
        }

        Assert.True(File.Exists(source));               // source untouched
        Assert.False(File.Exists(destination));         // nothing archived
        Assert.Empty(Directory.GetFiles(archive.Path, "*.archtmp", SearchOption.AllDirectories));
    }
}
