using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http.Features;

namespace Picer.ModelServer;

/// <summary>One reusable transport and a bounded shared GPU admission queue. Inference is never replayed.</summary>
public sealed class ModelInferenceRelay : IDisposable
{
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _admission;
    private readonly SemaphoreSlim _active;
    private readonly TimeSpan _queueTimeout;
    private readonly TimeProvider _clock;
    private readonly object _circuit = new();
    private int _failures;
    private DateTimeOffset _retryAt;
    private long _generation;

    public ModelInferenceRelay(HttpClient client, int concurrency = 2, int capacity = 8,
        TimeSpan? queueTimeout = null, TimeProvider? clock = null)
    {
        if (concurrency is < 1 or > 8 || capacity < concurrency || capacity > 64) throw new ArgumentOutOfRangeException(nameof(capacity));
        _client = client; _active = new(concurrency, concurrency); _admission = new(capacity, capacity);
        _queueTimeout = queueTimeout ?? TimeSpan.FromSeconds(15); _clock = clock ?? TimeProvider.System;
    }

    public async Task InvokeAsync(HttpContext context, Uri endpoint, string? key, TimeSpan timeout)
    {
        if (endpoint.Scheme is not ("http" or "https")) throw new ArgumentException("invalid_model_endpoint");
        if (context.Request.ContentLength > 16 * 1024 * 1024) { context.Response.StatusCode = 413; return; }
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = 16 * 1024 * 1024;
        if (!await _admission.WaitAsync(0, context.RequestAborted)) { await BusyAsync(context); return; }
        var entered = false;
        var generation = 0L;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(timeout);
        try
        {
            if (CoolingDown()) { await BusyAsync(context); return; }
            entered = await _active.WaitAsync(_queueTimeout, deadline.Token);
            if (!entered || CoolingDown()) { await BusyAsync(context); return; }
            lock (_circuit) generation = _generation;
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StreamContent(context.Request.Body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            if (response.Headers.RetryAfter is { } retry) context.Response.Headers.RetryAfter = retry.ToString();
            await response.Content.CopyToAsync(context.Response.Body, deadline.Token);
            RecordOutcome((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests, generation);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { context.Abort(); }
        catch (OperationCanceledException)
        {
            if (entered) RecordOutcome(true, generation);
            await FailAsync(context, 504, "model_deadline_exceeded");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            if (entered) RecordOutcome(true, generation);
            await FailAsync(context, 502, "model_transport_unavailable");
        }
        finally { if (entered) _active.Release(); _admission.Release(); }
    }

    private bool CoolingDown() { lock (_circuit) return _clock.GetUtcNow() < _retryAt; }
    private void RecordOutcome(bool failed, long generation)
    {
        lock (_circuit)
        {
            // A response from before the circuit opened must not cancel its cooldown.
            if (generation != _generation) return;
            if (!failed) { _failures = 0; return; }
            if (++_failures < 3) return;
            _retryAt = _clock.GetUtcNow().AddSeconds(10); _failures = 0; _generation++;
        }
    }
    private static Task BusyAsync(HttpContext context)
    { context.Response.Headers.RetryAfter = "10"; return FailAsync(context, 503, "model_capacity_busy"); }
    private static async Task FailAsync(HttpContext context, int status, string error)
    {
        if (context.Response.HasStarted) { context.Abort(); return; }
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error }, context.RequestAborted);
    }
    public void Dispose() { _client.Dispose(); _admission.Dispose(); _active.Dispose(); }
}
