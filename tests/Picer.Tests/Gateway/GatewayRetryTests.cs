using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Picer.Core.Gateway;

namespace Picer.Tests;

public sealed class GatewayRetryTests
{
    [Fact]
    public async Task BusyResponseHonorsRetryAfterThenRecovers()
    {
        var calls = 0;
        using var handler = new Handler(() => Interlocked.Increment(ref calls) == 1 ? Busy(1) : Success());
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/") };
        using var gateway = new HttpIndexGateway(client);
        var elapsed = Stopwatch.StartNew();
        Assert.Empty(await gateway.ListSourcesAsync());
        Assert.Equal(2, calls);
        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(950));
    }

    [Fact]
    public async Task CancellationDuringCooldownPreventsAnotherRequest()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var handler = new Handler(() => { Interlocked.Increment(ref calls); received.TrySetResult(); return Busy(20); });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/") };
        using var gateway = new HttpIndexGateway(client);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = gateway.ListSourcesAsync(stop.Token);
        await received.Task;
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task LongServerCooldownReturnsBusyWithoutEarlyRetry()
    {
        var calls = 0;
        using var handler = new Handler(() => { calls++; return Busy(60); });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/") };
        using var gateway = new HttpIndexGateway(client);
        await Assert.ThrowsAsync<HttpRequestException>(() => gateway.ListSourcesAsync());
        Assert.Equal(1, calls);
    }

    private static HttpResponseMessage Busy(int seconds)
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("busy") };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return response;
    }
    private static HttpResponseMessage Success() => new(HttpStatusCode.OK) { Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond());
    }
}
