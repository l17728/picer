using System.Net;
using Microsoft.AspNetCore.Http;
using Picer.ModelServer;

namespace Picer.Tests;

public sealed class ModelInferenceRelayTests
{
    [Fact]
    public async Task OverloadIsBoundedAndCancellationReturnsCapacity()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var client = new HttpClient(new Handler(async token =>
        {
            if (Interlocked.Increment(ref calls) == 1) { started.SetResult(); await Task.Delay(Timeout.Infinite, token); }
            return new(HttpStatusCode.OK) { Content = new StringContent("ok") };
        }));
        using var relay = new ModelInferenceRelay(client, concurrency: 1, capacity: 1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = Context(stop.Token);
        var pending = relay.InvokeAsync(first, Endpoint, null, TimeSpan.FromSeconds(10));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var rejected = Context(); await relay.InvokeAsync(rejected, Endpoint, null, TimeSpan.FromSeconds(1));
        Assert.Equal(503, rejected.Response.StatusCode); Assert.Equal(1, calls);
        stop.Cancel(); await pending;
        var recovered = Context(); await relay.InvokeAsync(recovered, Endpoint, null, TimeSpan.FromSeconds(1));
        Assert.Equal(200, recovered.Response.StatusCode); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RepeatedFailuresOpenCircuitAndRecoveryClosesItWithoutReplayingInference()
    {
        var clock = new Clock(); var calls = 0;
        using var client = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(
            ++calls <= 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent("{}") })));
        using var relay = new ModelInferenceRelay(client, clock: clock);
        for (var i = 0; i < 3; i++) await relay.InvokeAsync(Context(), Endpoint, null, TimeSpan.FromSeconds(1));
        var cooling = Context(); await relay.InvokeAsync(cooling, Endpoint, null, TimeSpan.FromSeconds(1));
        Assert.Equal(503, cooling.Response.StatusCode); Assert.Equal(3, calls);
        clock.Now += TimeSpan.FromSeconds(11);
        var recovered = Context(); await relay.InvokeAsync(recovered, Endpoint, null, TimeSpan.FromSeconds(1));
        Assert.Equal(200, recovered.Response.StatusCode); Assert.Equal(4, calls);
    }

    [Fact]
    public async Task RequestDeadlineCancelsTransportAndReleasesSlot()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(async token =>
        {
            if (++calls == 1) await Task.Delay(Timeout.Infinite, token);
            return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }));
        using var relay = new ModelInferenceRelay(client, concurrency: 1, capacity: 1);
        var timedOut = Context(); await relay.InvokeAsync(timedOut, Endpoint, null, TimeSpan.FromMilliseconds(100));
        Assert.Equal(504, timedOut.Response.StatusCode);
        var recovered = Context(); await relay.InvokeAsync(recovered, Endpoint, null, TimeSpan.FromSeconds(1));
        Assert.Equal(200, recovered.Response.StatusCode);
    }

    private static readonly Uri Endpoint = new("http://localhost/inference");
    private static DefaultHttpContext Context(CancellationToken token = default)
    { var context = new DefaultHttpContext { RequestAborted = token }; context.Request.Body = new MemoryStream("{}"u8.ToArray()); context.Response.Body = new MemoryStream(); return context; }
    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(cancellationToken);
    }
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
}
