using Picer.Core.Gateway;

namespace Picer.Tests;

public sealed class ForegroundWorkPriorityTests
{
    [Fact]
    public async Task RequiredSyncUnblocksExistingWaitersAndRestoresPriorityAfterLastLease()
    {
        var priority = new ForegroundWorkPriority();
        using var foreground = priority.Enter();
        var waiting = priority.WaitAsync(CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        var first = priority.RequireBackgroundProgress();
        var second = priority.RequireBackgroundProgress();
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        first.Dispose();
        first.Dispose();
        await priority.WaitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        second.Dispose();
        var deferred = priority.WaitAsync(CancellationToken.None);
        Assert.False(deferred.IsCompleted);
        foreground.Dispose();
        await deferred.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task BackgroundResumesOnlyAfterLastForegroundLeaseAndCancellationDoesNotReleaseOthers()
    {
        var priority = new ForegroundWorkPriority();
        var first = priority.Enter();
        var second = priority.Enter();
        var background = priority.WaitAsync(CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var cancelled = priority.WaitAsync(cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        first.Dispose(); first.Dispose();
        Assert.False(background.IsCompleted);
        second.Dispose();
        await background.WaitAsync(TimeSpan.FromSeconds(2));
        await priority.WaitAsync(CancellationToken.None);
    }
}
