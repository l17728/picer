using Picer.Core.Gateway;

namespace Picer.Tests.Gateway;

public sealed class ThumbnailRetryTrackerTests
{
    [Fact]
    public void TransientFailuresUseBoundedExponentialBackoffAndSuccessResetsIt()
    {
        var tracker = new ThumbnailRetryTracker(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8));
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(TimeSpan.FromSeconds(2), tracker.RecordFailure(12, now));
        Assert.False(tracker.CanRetry(12, now.AddSeconds(1)));
        Assert.True(tracker.CanRetry(12, now.AddSeconds(2)));
        Assert.Equal(TimeSpan.FromSeconds(4), tracker.RecordFailure(12, now.AddSeconds(2)));
        Assert.Equal(TimeSpan.FromSeconds(8), tracker.RecordFailure(12, now.AddSeconds(6)));
        Assert.Equal(TimeSpan.FromSeconds(8), tracker.RecordFailure(12, now.AddSeconds(14)));

        tracker.RecordSuccess(12);
        Assert.True(tracker.CanRetry(12, now));
        Assert.Equal(TimeSpan.FromSeconds(2), tracker.RecordFailure(12, now));
    }

    [Fact]
    public void FailuresAreTrackedIndependentlyPerImage()
    {
        var tracker = new ThumbnailRetryTracker(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4));
        var now = DateTimeOffset.UtcNow;
        tracker.RecordFailure(1, now);

        Assert.False(tracker.CanRetry(1, now));
        Assert.True(tracker.CanRetry(2, now));
    }
}
