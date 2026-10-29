using System.Collections.Concurrent;
using System.Net.WebSockets;
using Picer.Core.Gateway;

namespace Picer.IndexService;

internal sealed partial class AgentConnectionRegistry
{
    private sealed class MediaLease(string agentId)
    {
        public string AgentId { get; } = agentId;
        public TaskCompletionSource<WebSocket> Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly ConcurrentDictionary<string, MediaLease> _media = new();
    private readonly SemaphoreSlim _mediaSlots = new(4, 4);
    private readonly SemaphoreSlim _keyframeSlots = new(2, 2);
    private readonly SemaphoreSlim _keyframeAdmission = new(4, 4);
    private readonly SemaphoreSlim _previewSlots = new(2, 2);
    private readonly SemaphoreSlim _analysisSlots = new(2, 2);
    private readonly SemaphoreSlim _mediaAdmission = new(8, 8);
    private readonly SemaphoreSlim _previewAdmission = new(4, 4);
    private readonly SemaphoreSlim _analysisAdmission = new(4, 4);

    public async Task ClearMediaCachesAsync(CancellationToken token)
    {
        ClearPooledMedia();
        foreach (var session in _sessions.Values)
        {
            try { await session.SendAsync(new { type = "media.cache.clear" }, token); }
            catch (WebSocketException) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
    }

    public async Task HandleMediaAsync(HttpContext context, string ticket)
    {
        if (!context.WebSockets.IsWebSocketRequest || !_media.TryGetValue(ticket, out var candidate) ||
            _strictRouting && candidate.AgentId != context.Request.Headers["X-Picer-Agent-Grant"].ToString() ||
            !_media.TryRemove(new KeyValuePair<string, MediaLease>(ticket, candidate)))
        { context.Response.StatusCode = 404; return; }
        var lease = candidate;
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        if (!lease.Connected.TrySetResult(socket)) return;
        using var aborted = context.RequestAborted.Register(() => { try { SafeAbort(socket); } finally { lease.Closed.TrySetResult(); } });
        await lease.Closed.Task;
    }

    public async Task<SocketMediaStream?> OpenMediaAsync(string? agentId, string path, int previewSize, CancellationToken token, string? expectedETag = null, bool keepLease = false,
        MediaWire.SourceFingerprint? sourceFingerprint = null, bool keyframe = false)
    {
        var admission = keyframe ? _keyframeAdmission : previewSize == 1280 ? _analysisAdmission : previewSize > 0 ? _previewAdmission : _mediaAdmission;
        if (!await admission.WaitAsync(0, token)) throw new MediaChannelBusyException();
        try { return await OpenMediaCoreAsync(agentId, path, previewSize, token, expectedETag, keepLease, sourceFingerprint, keyframe); }
        finally { admission.Release(); }
    }

    private async Task<SocketMediaStream?> OpenMediaCoreAsync(string? agentId, string path, int previewSize, CancellationToken token, string? expectedETag, bool keepLease,
        MediaWire.SourceFingerprint? sourceFingerprint, bool keyframe)
    {
        if (string.IsNullOrWhiteSpace(agentId) && _sessions.Count > 1) throw new IOException("media_agent_selection_required");
        var session = await WaitForSessionAsync(agentId, token);
        if (session is null || !session.Capabilities.Contains(MediaWire.Capability)) return null;
        if (previewSize == 1280 && !session.Capabilities.Contains(MediaWire.AnalysisInputCapability)) return null;
        var slots = keyframe ? _keyframeSlots : previewSize == 1280 ? _analysisSlots : previewSize > 0 ? _previewSlots : _mediaSlots;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, session.Disconnected);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var reusable = !keyframe && previewSize == 0 && session.Capabilities.Contains(MediaWire.ReuseCapability)
            && (keepLease || expectedETag?.StartsWith("\"lease-", StringComparison.Ordinal) == true);
        long generation;
        lock (_mediaPoolLock)
        {
            if (_mediaStopped) throw new IOException("media_service_stopping");
            generation = _mediaGeneration;
        }
        if (reusable && TakeMedia(session, path, expectedETag) is { } pooled)
        {
            try
            {
                await MediaWire.SendJsonAsync(pooled.Socket, new MediaWire.ReadRequest(0, 0), deadline.Token);
                var checkedMetadata = ValidateMediaMetadata(await MediaWire.ReceiveJsonAsync<MediaWire.Metadata>(pooled.Socket, deadline.Token));
                if (checkedMetadata != pooled.Metadata) throw new MediaSourceChangedException();
                return BorrowMedia(pooled);
            }
            catch (MediaSourceChangedException) { RetireMedia(pooled); throw; }
            catch (OperationCanceledException) { RetireMedia(pooled); throw; }
            catch (Exception exception) when (exception is IOException or WebSocketException or ObjectDisposedException)
            { RetireMedia(pooled); }
        }
        if (!keyframe && previewSize == 0) Interlocked.Increment(ref _mediaWaiters);
        try
        {
            if (!keyframe && previewSize == 0 && slots.CurrentCount == 0) EvictIdleMedia();
            if (!await slots.WaitAsync(TimeSpan.FromSeconds(15), deadline.Token))
                throw new MediaChannelBusyException();
        }
        finally { if (!keyframe && previewSize == 0) Interlocked.Decrement(ref _mediaWaiters); }
        var ticket = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var lease = new MediaLease(session.AgentId);
        _media[ticket] = lease;
        var transferred = false;
        WebSocket? socket = null;
        try
        {
            await session.SendAsync(new { type = "media.open", requestId = ticket, path, previewSize, expectedETag, keepLease, sourceFingerprint, keyframe }, deadline.Token);
            socket = await lease.Connected.Task.WaitAsync(deadline.Token);
            var metadata = ValidateMediaMetadata(await MediaWire.ReceiveJsonAsync<MediaWire.Metadata>(socket, deadline.Token));
            SocketMediaStream result;
            if (reusable)
            {
                var entry = new ReusableMedia(session, path, socket, metadata, generation,
                    () => { lease.Closed.TrySetResult(); slots.Release(); });
                lock (_mediaPoolLock)
                {
                    if (_mediaStopped || generation != _mediaGeneration) throw new IOException("media_pool_invalidated");
                    _mediaPool.Add(entry);
                }
                result = BorrowMedia(entry);
            }
            else result = new SocketMediaStream(socket, metadata, () => { lease.Closed.TrySetResult(); slots.Release(); });
            transferred = true;
            result.CancelWhen(session.Disconnected);
            session.Disconnected.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            _media.TryRemove(ticket, out _);
            if (!transferred)
            {
                lease.Connected.TrySetCanceled();
                try { SafeAbort(socket); }
                finally { lease.Closed.TrySetResult(); slots.Release(); }
            }
        }
    }

    private static void SafeAbort(WebSocket? socket)
    {
        try { socket?.Abort(); }
        catch (Exception exception) when (exception is ObjectDisposedException or WebSocketException) { }
    }
    public async Task ManageMediaLeaseAsync(string etag, bool release, CancellationToken token)
    {
        if (etag.Length > 128 || !etag.StartsWith("\"lease-", StringComparison.Ordinal) || !etag.EndsWith('"')) return;
        if (release) ClearPooledMedia(etag: etag);
        foreach (var session in _sessions.Values)
        {
            try { await session.SendAsync(new { type = release ? "media.release" : "media.keepalive", etag }, token); }
            catch (WebSocketException) { }
        }
    }
}

internal sealed class MediaChannelBusyException() : IOException("media_channel_busy");
