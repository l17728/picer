using Picer.Core.Indexing;

namespace Picer.Tests;

public sealed class CoalescingSyncWorkerTests
{
    [Fact]
    public async Task ThousandsOfWakeupsStaySerializedAndDisposeDrainsActiveWork()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0; var calls = 0; var maximum = 0;
        var worker = new CoalescingSyncWorker(async token =>
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref active));
            try
            {
                if (Interlocked.Increment(ref calls) == 1) { entered.SetResult(); await release.Task.WaitAsync(token); }
                else { second.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            }
            finally { Interlocked.Decrement(ref active); }
        });
        try
        {
            worker.Request(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var index = 0; index < 10000; index++) worker.Request();
            release.SetResult(); await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { worker.Dispose(); }
        Assert.Equal(1, maximum); Assert.Equal(2, calls); Assert.Equal(0, active);
    }
}
