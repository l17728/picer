using Picer.Core.Data;
using Picer.Core.Video;

namespace Picer.Tests;

public sealed class DescriptionPerformanceTests
{
    [Theory]
    [InlineData("2026-10-29T01:02:03.0000001Z", "2026-10-29T09:02:03.0000001+08:00", true)]
    [InlineData("2026-10-29T01:02:03.0000001Z", "2026-10-29T01:02:03.0000002Z", false)]
    [InlineData("2026-10-29T01:02:03", "2026-10-29T01:02:03Z", true)]
    [InlineData("01:02:03", "01:02:03", false)]
    [InlineData("", "", false)]
    [InlineData("not a time", "not a time", false)]
    public void TimestampComparisonPreservesPrecisionAndRejectsIncompleteInput(string first, string second, bool equal)
    {
        Assert.Equal(equal, SourceTimestamp.SameInstant(first, second));
        using var db = new PicerDatabase(":memory:");
        using var query = db.Command("SELECT picer_same_instant($first,$second)");
        query.Parameters.AddWithValue("$first", first); query.Parameters.AddWithValue("$second", second);
        Assert.Equal(equal ? 1L : 0L, query.ExecuteScalar());
    }

    [Fact]
    public async Task LimitedFrameDownloadReplacesUnavailableFramesWithoutLosingOrdinal()
    {
        var requested = new System.Collections.Concurrent.ConcurrentBag<int>();
        var frames = Enumerable.Range(0, 100).Select(i => i.ToString()).ToArray();
        var result = await VideoFrameSelection.MaterializeAsync(frames, 12, (frame, ordinal) =>
        { requested.Add(ordinal); return Task.FromResult(ordinal < 2 ? null : frame); }, CancellationToken.None);
        Assert.Equal(Enumerable.Range(2, 12).Select(i => i.ToString()), result);
        Assert.Equal(Enumerable.Range(0, 14), requested.Order());
    }

    [Fact]
    public async Task NormalFrameViewingDoesNotWaitForSlowBatchBeforeFillingSlots()
    {
        var slow = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fifth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = Enumerable.Range(0, 9).Select(i => i.ToString()).ToArray();
        var all = VideoFrameSelection.MaterializeAsync(frames, int.MaxValue, (frame, ordinal) =>
        { if (ordinal == 4) fifth.TrySetResult(); return ordinal == 0 ? slow.Task : Task.FromResult<string?>(frame); }, CancellationToken.None);
        try { await fifth.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { slow.TrySetResult("0"); }
        Assert.Equal(frames, await all);
    }
}
