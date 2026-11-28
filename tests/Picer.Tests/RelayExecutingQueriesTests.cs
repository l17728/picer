using Picer.IndexService.Identity;

namespace Picer.Tests;

public sealed class RelayExecutingQueriesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelOrExpiryInterruptsRunningQueryWithoutStoppingSibling(bool expire)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var queries = new RelayExecutingQueries(deadline.Token, 2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sibling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = Guid.NewGuid();
        var operation = Guid.NewGuid();
        var expires = DateTimeOffset.UtcNow.AddMinutes(1);
        Assert.True(queries.TryStart(request, operation, expire ? DateTimeOffset.UtcNow.AddSeconds(1) : expires, async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { interrupted.SetResult(); throw; }
        }));
        await entered.Task.WaitAsync(deadline.Token);
        Assert.True(queries.TryStart(Guid.NewGuid(), Guid.NewGuid(), expires, async token =>
        {
            await release.Task.WaitAsync(token);
            sibling.SetResult();
        }));
        Assert.False(queries.TryStart(Guid.NewGuid(), Guid.NewGuid(), expires, _ => Task.CompletedTask));
        if (!expire)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => queries.CancelAsync(request, Guid.NewGuid()));
            Assert.False(interrupted.Task.IsCompleted);
            await queries.CancelAsync(request, operation);
        }
        await interrupted.Task.WaitAsync(deadline.Token);
        Assert.False(queries.SessionToken.IsCancellationRequested);
        release.SetResult();
        await sibling.Task.WaitAsync(deadline.Token);
    }
}
