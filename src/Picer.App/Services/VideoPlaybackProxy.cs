using System.IO;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Picer.Core.Gateway;

namespace Picer.App.Services;

public sealed class VideoPlaybackProxy : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _requests = new(4, 4);
    private readonly TaskCompletionSource _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ParallelMediaReader _reader;
    private readonly string _path = "/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant() + "/video";
    private DateTime _lastRead = DateTime.UtcNow;
    private Task? _expiration;
    public string Url { get; private set; } = "";
    public Task FirstRead => _read.Task;
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    private long _bytesSent;
    private int _activeRequests;
    private VideoPlaybackProxy(ParallelMediaReader reader)
    {
        _reader = reader;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.Run(HandleAsync);
    }
    public static async Task<VideoPlaybackProxy> StartAsync(ParallelMediaReader reader, CancellationToken token)
    {
        var proxy = new VideoPlaybackProxy(reader);
        try
        {
            await proxy._app.StartAsync(token);
            proxy.Url = proxy._app.Urls.Single() + proxy._path;
            proxy._expiration = proxy.ExpireAsync();
            return proxy;
        }
        catch { await proxy.DisposeAsync(); throw; }
    }
    private async Task ExpireAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token);
                if (Volatile.Read(ref _activeRequests) == 0 && DateTime.UtcNow - _lastRead > TimeSpan.FromMinutes(30)) { _stop.Cancel(); await _app.StopAsync(); return; }
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task HandleAsync(HttpContext context)
    {
        var uri = new Uri(Url);
        if (!IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None)
            || !string.Equals(context.Request.Host.Value, uri.Authority, StringComparison.Ordinal)
            || context.Request.Path.Value != _path || context.Request.QueryString.HasValue
            || context.Request.Headers.ContainsKey("Origin") || context.Request.Headers.ContainsKey("Sec-Fetch-Site")
            || !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
        { context.Response.StatusCode = 403; return; }
        using var token = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stop.Token);
        await _requests.WaitAsync(token.Token);
        Interlocked.Increment(ref _activeRequests);
        try
        {
            _lastRead = DateTime.UtcNow;
            var info = _reader.Info;
            var response = context.Response;
            response.Headers.ETag = info.ETag;
            response.Headers.AcceptRanges = "bytes";
            response.Headers.CacheControl = "no-store";
            response.ContentType = info.ContentType;
            if (context.Request.Headers.IfMatch.Count > 0 && context.Request.Headers.IfMatch.ToString() != info.ETag)
            { response.StatusCode = 412; return; }
            long offset = 0, count = info.Length;
            var range = context.Request.Headers.Range.ToString();
            if (range.Length > 0 && (context.Request.Headers.IfRange.Count == 0 || context.Request.Headers.IfRange.ToString() == info.ETag))
            {
                if (!RangeHeaderValue.TryParse(range, out var parsed) || parsed.Unit != "bytes" || parsed.Ranges.Count != 1 || info.Length == 0)
                { response.StatusCode = 416; response.Headers.ContentRange = $"bytes */{info.Length}"; return; }
                var selected = parsed.Ranges.Single();
                offset = selected.From ?? Math.Max(0, info.Length - (selected.To ?? 0));
                var end = selected.From.HasValue ? Math.Min(selected.To ?? (info.Length - 1), info.Length - 1) : info.Length - 1;
                if (offset < 0 || offset >= info.Length || end < offset)
                { response.StatusCode = 416; response.Headers.ContentRange = $"bytes */{info.Length}"; return; }
                count = end - offset + 1;
                response.StatusCode = 206;
                response.Headers.ContentRange = $"bytes {offset}-{end}/{info.Length}";
            }
            response.ContentLength = count;
            if (HttpMethods.IsHead(context.Request.Method)) return;
            // Confirm real upstream bytes before treating the player as a network reader.
            if (count > 0)
            {
                var initial = (int)Math.Min(count, MediaWire.BlockSize - offset % MediaWire.BlockSize);
                await _reader.CopyRangeAsync(response.Body, offset, initial, token.Token);
                await response.Body.FlushAsync(token.Token);
                Interlocked.Add(ref _bytesSent, initial);
                _read.TrySetResult();
                await _reader.CopyRangeAsync(response.Body, offset + initial, count - initial, token.Token);
                Interlocked.Add(ref _bytesSent, count - initial);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Net.Http.HttpRequestException or OperationCanceledException)
        { context.Abort(); }
        finally { _lastRead = DateTime.UtcNow; Interlocked.Decrement(ref _activeRequests); _requests.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _app.StopAsync();
        if (_expiration is not null) await _expiration;
        await _app.DisposeAsync();
        _stop.Dispose();
    }
}
