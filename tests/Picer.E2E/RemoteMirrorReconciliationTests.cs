using System.Net.Http;
using Picer.App;

namespace Picer.E2E;

public sealed class RemoteMirrorReconciliationTests
{
    [Fact]
    public async Task RetryWorkerKeepsRetryingWithBoundedBackoffUntilSyncSucceeds()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var failures = new List<Type>();
        var successes = 0;

        await AppServices.RetryRemoteMirrorUntilSuccessAsync(_ =>
        {
            if (Interlocked.Increment(ref attempts) < 3)
                throw new HttpRequestException("temporary network failure");
            return Task.CompletedTask;
        }, CancellationToken.None,
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; },
            exception => failures.Add(exception.GetType()),
            () => successes++);

        Assert.Equal(3, attempts);
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)], delays);
        Assert.Equal([typeof(HttpRequestException), typeof(HttpRequestException)], failures);
        Assert.Equal(1, successes);
    }

    [Fact]
    public async Task RetryWorkerStopsWhenItsLifetimeIsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;

        await AppServices.RetryRemoteMirrorUntilSuccessAsync(_ =>
        {
            attempts++;
            throw new HttpRequestException("still offline");
        }, cancellation.Token,
            (_, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(token);
            });

        Assert.Equal(0, attempts);
    }
}
