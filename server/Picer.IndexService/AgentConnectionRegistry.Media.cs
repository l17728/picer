using System.Collections.Concurrent;
using System.Net.WebSockets;
using Picer.Core.Gateway;

namespace Picer.IndexService;

internal sealed partial class AgentConnectionRegistry
{
    private sealed class MediaLease
    {
        public TaskCompletionSource<WebSocket> Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly ConcurrentDictionary<string, MediaLease> _media = new();
    private readonly SemaphoreSlim _mediaSlots = new(4, 4);
    private readonly SemaphoreSlim _previewSlots = new(2, 2);

    public async Task ClearMediaCachesAsync(CancellationToken token)
    {
        foreach (var session in _sessions.Values)
        {
            try { await session.SendAsync(new { type = "media.cache.clear" }, token); }
            catch (WebSocketException) { }
        }
    }

    public async Task HandleMediaAsync(HttpContext context, string ticket)
    {
        if (!context.WebSockets.IsWebSocketRequest || !_media.TryRemove(ticket, out var lease))
        { context.Response.StatusCode = 404; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        if (!lease.Connected.TrySetResult(socket)) return;
        using var aborted = context.RequestAborted.Register(() => { socket.Abort(); lease.Closed.TrySetResult(); });
        await lease.Closed.Task;
    }

    public async Task<SocketMediaStream?> OpenMediaAsync(string? agentId, string path, int previewSize, CancellationToken token, string? expectedETag = null, bool keepLease = false)
    {
        if (string.IsNullOrWhiteSpace(agentId) && _sessions.Count > 1) throw new IOException("media_agent_selection_required");
        var session = await WaitForSessionAsync(agentId, token);
        if (session is null || !session.Capabilities.Contains(MediaWire.Capability)) return null;
        var slots = previewSize > 0 ? _previewSlots : _mediaSlots;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        await slots.WaitAsync(deadline.Token);
        var ticket = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var lease = new MediaLease();
        _media[ticket] = lease;
        var transferred = false;
        WebSocket? socket = null;
        try
        {
            await session.SendAsync(new { type = "media.open", requestId = ticket, path, previewSize, expectedETag, keepLease }, deadline.Token);
            socket = await lease.Connected.Task.WaitAsync(deadline.Token);
            var metadata = await MediaWire.ReceiveJsonAsync<MediaWire.Metadata>(socket, deadline.Token);
            if (metadata.Error == "media_source_changed") throw new MediaSourceChangedException();
            if (metadata.Error is not null || metadata.Length < 0 || metadata.ETag.Length is < 3 or > 128)
                throw new IOException("media_source_unavailable");
            var result = new SocketMediaStream(socket, metadata, () => { lease.Closed.TrySetResult(); slots.Release(); });
            transferred = true;
            return result;
        }
        finally
        {
            _media.TryRemove(ticket, out _);
            if (!transferred)
            {
                lease.Connected.TrySetCanceled();
                socket?.Abort();
                lease.Closed.TrySetResult();
                slots.Release();
            }
        }
    }
    public async Task ManageMediaLeaseAsync(string etag, bool release, CancellationToken token)
    {
        if (etag.Length > 128 || !etag.StartsWith("\"lease-", StringComparison.Ordinal) || !etag.EndsWith('"')) return;
        foreach (var session in _sessions.Values)
        {
            try { await session.SendAsync(new { type = release ? "media.release" : "media.keepalive", etag }, token); }
            catch (WebSocketException) { }
        }
    }
}
