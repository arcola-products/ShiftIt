using ShiftIt.Configuration;
using ShiftIt.Services;
using ShiftIt.Tests.TestHelpers;

namespace ShiftIt.Tests;

public sealed class FailureTrackerTests
{
    private static FailureTracker CreateTracker(int maxFileFailures)
    {
        var options = new ArchiveOptions { MaxFileFailures = maxFileFailures };
        return new FailureTracker(new StaticOptionsMonitor<ArchiveOptions>(options));
    }

    private static readonly DateTime Stamp = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void QuarantinesFile_OnceFailureCountReachesMax()
    {
        var tracker = CreateTracker(maxFileFailures: 3);
        const string path = @"C:\hot\bad.txt";

        Assert.False(tracker.IsQuarantined(path, Stamp)); // unknown file
        Assert.Equal(1, tracker.RecordFailure(path, Stamp));
        Assert.Equal(2, tracker.RecordFailure(path, Stamp));
        Assert.False(tracker.IsQuarantined(path, Stamp)); // still below the threshold
        Assert.Equal(3, tracker.RecordFailure(path, Stamp));
        Assert.True(tracker.IsQuarantined(path, Stamp));  // reached the threshold
    }

    [Fact]
    public void Clear_ResetsFailures()
    {
        var tracker = CreateTracker(maxFileFailures: 2);
        const string path = @"C:\hot\bad.txt";

        tracker.RecordFailure(path, Stamp);
        tracker.RecordFailure(path, Stamp);
        Assert.True(tracker.IsQuarantined(path, Stamp));

        tracker.Clear(path);
        Assert.False(tracker.IsQuarantined(path, Stamp));
        Assert.Equal(1, tracker.RecordFailure(path, Stamp)); // count started over
    }

    [Fact]
    public void ModifiedFile_GetsAFreshStart()
    {
        var tracker = CreateTracker(maxFileFailures: 2);
        const string path = @"C:\hot\bad.txt";

        tracker.RecordFailure(path, Stamp);
        tracker.RecordFailure(path, Stamp);
        Assert.True(tracker.IsQuarantined(path, Stamp));

        // A newer last-write time means the file changed: no longer quarantined.
        var newer = Stamp.AddMinutes(1);
        Assert.False(tracker.IsQuarantined(path, newer));
        Assert.Equal(1, tracker.RecordFailure(path, newer)); // reset to a single failure
    }

    [Fact]
    public void MaxFileFailuresZero_DisablesQuarantine()
    {
        var tracker = CreateTracker(maxFileFailures: 0);
        const string path = @"C:\hot\bad.txt";

        tracker.RecordFailure(path, Stamp);
        tracker.RecordFailure(path, Stamp);
        tracker.RecordFailure(path, Stamp);
        Assert.False(tracker.IsQuarantined(path, Stamp)); // never quarantined
    }
}
