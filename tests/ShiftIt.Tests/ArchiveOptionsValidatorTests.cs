using ShiftIt.Configuration;

namespace ShiftIt.Tests;

public sealed class ArchiveOptionsValidatorTests
{
    private static readonly string Root = Path.GetTempPath();

    private static (bool Succeeded, string? Failure) Run(ArchiveOptions options)
    {
        var result = new ArchiveOptionsValidator().Validate(name: null, options);
        return (result.Succeeded, result.FailureMessage);
    }

    private static ArchiveOptions With(string hot, string archive) => new()
    {
        Pairs = [new TargetPair { Name = "P", HotRoot = hot, ArchiveRoot = archive }],
    };

    [Fact]
    public void Fails_WhenNoPairs()
    {
        var (ok, _) = Run(new ArchiveOptions { Pairs = [] });
        Assert.False(ok);
    }

    [Fact]
    public void Fails_WhenHotRootIsRelative()
    {
        var (ok, msg) = Run(With("relative\\hot", Path.Combine(Root, "archive")));
        Assert.False(ok);
        Assert.Contains("HotRoot", msg);
    }

    [Fact]
    public void Fails_WhenArchiveRootIsRelative()
    {
        var (ok, msg) = Run(With(Path.Combine(Root, "hot"), "relative\\archive"));
        Assert.False(ok);
        Assert.Contains("ArchiveRoot", msg);
    }

    [Fact]
    public void Fails_WhenRootsAreEqual()
    {
        var same = Path.Combine(Root, "same");
        var (ok, msg) = Run(With(same, same));
        Assert.False(ok);
        Assert.Contains("different", msg);
    }

    [Fact]
    public void Fails_WhenArchiveNestedInsideHot()
    {
        var hot = Path.Combine(Root, "hot");
        var archive = Path.Combine(hot, "inner", "archive");
        var (ok, msg) = Run(With(hot, archive));
        Assert.False(ok);
        Assert.Contains("inside", msg);
    }

    [Fact]
    public void Succeeds_ForValidDistinctRootedPaths()
    {
        var (ok, _) = Run(With(Path.Combine(Root, "hot"), Path.Combine(Root, "archive")));
        Assert.True(ok);
    }
}
