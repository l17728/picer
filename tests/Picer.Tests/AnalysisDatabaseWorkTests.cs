using Picer.Core.Logging;
using Xunit;

namespace Picer.Tests;

public sealed class AnalysisDatabaseWorkTests
{
    [Fact]
    public async Task CancellationWhileQueuedDoesNotExecuteWork()
    {
        using var cancellation = new CancellationTokenSource();
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var task = AnalysisDatabaseWork.RunAsync("test_queue", () => calls++, cancellation.Token,
            async (work, token) =>
            {
                queued.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                work();
            });
        await queued.Task;
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DurableWorkIsAwaitedUntilCommitted()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = false;
        var task = AnalysisDatabaseWork.RunAsync("test_save", () => saved = true, CancellationToken.None,
            async (work, token) =>
            {
                Assert.False(token.CanBeCanceled);
                queued.SetResult();
                await release.Task;
                work();
            });
        await queued.Task;
        Assert.False(task.IsCompleted);
        Assert.False(saved);
        release.SetResult();
        await task;
        Assert.True(saved);
    }

    [Fact]
    public async Task DiagnosticsPreserveOriginalDatabaseFailure()
    {
        var failure = new IOException("test_failure");
        var observed = await Assert.ThrowsAsync<IOException>(() => AnalysisDatabaseWork.RunAsync(
            "test_failure", () => throw failure, CancellationToken.None,
            (work, _) => { work(); return Task.CompletedTask; }));
        Assert.Same(failure, observed);
    }
}
