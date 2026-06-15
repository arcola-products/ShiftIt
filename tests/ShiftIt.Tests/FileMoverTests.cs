using Microsoft.Extensions.Logging.Abstractions;
using ShiftIt.Services;
using ShiftIt.Tests.TestHelpers;

namespace ShiftIt.Tests;

public sealed class FileMoverTests
{
    private static FileMover CreateMover() => new(NullLogger<FileMover>.Instance);

    [Fact]
    public async Task MoveAsync_MovesFile_AndCreatesDestinationDirectory()
    {
        using var temp = new TempDir();
        var source = temp.WriteFile("hot/file.txt", "hello");
        var destination = temp.Combine("archive", "nested", "file.txt");

        var result = await CreateMover().MoveAsync(source, destination, verifyWithHash: false, CancellationToken.None);

        Assert.Equal(MoveResult.Moved, result);
        Assert.False(File.Exists(source));            // source removed last
        Assert.True(File.Exists(destination));        // mirrored dir created
        Assert.Equal("hello", File.ReadAllText(destination));
    }

    [Fact]
    public async Task MoveAsync_PreservesLastWriteTime()
    {
        using var temp = new TempDir();
        var source = temp.WriteFile("hot/file.txt");
        var stamp = DateTime.UtcNow.AddDays(-100);
        File.SetLastWriteTimeUtc(source, stamp);
        var destination = temp.Combine("archive", "file.txt");

        await CreateMover().MoveAsync(source, destination, verifyWithHash: false, CancellationToken.None);

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(destination), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task MoveAsync_SkipsAndKeepsSource_WhenDestinationExists()
    {
        using var temp = new TempDir();
        var source = temp.WriteFile("hot/file.txt", "incoming");
        var destination = temp.WriteFile("archive/file.txt", "existing");

        var result = await CreateMover().MoveAsync(source, destination, verifyWithHash: false, CancellationToken.None);

        Assert.Equal(MoveResult.SkippedExists, result);
        Assert.True(File.Exists(source));                            // source untouched
        Assert.Equal("existing", File.ReadAllText(destination));     // destination not overwritten
    }

    [Fact]
    public async Task MoveAsync_WithHashVerification_MovesAndMatchesContent()
    {
        using var temp = new TempDir();
        var content = new string('x', 50_000);
        var source = temp.WriteFile("hot/big.bin", content);
        var destination = temp.Combine("archive", "big.bin");

        var result = await CreateMover().MoveAsync(source, destination, verifyWithHash: true, CancellationToken.None);

        Assert.Equal(MoveResult.Moved, result);
        Assert.Equal(content, File.ReadAllText(destination));
    }

    [Fact]
    public async Task MoveAsync_LeavesNoTempFile_OnSuccess()
    {
        using var temp = new TempDir();
        var source = temp.WriteFile("hot/file.txt");
        var destination = temp.Combine("archive", "file.txt");

        await CreateMover().MoveAsync(source, destination, verifyWithHash: false, CancellationToken.None);

        var leftovers = Directory.GetFiles(temp.Path, "*.archtmp", SearchOption.AllDirectories);
        Assert.Empty(leftovers);
    }
}
