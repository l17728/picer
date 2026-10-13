using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Picer.IndexService.Identity;

/// <summary>The outer device authorization owns the full socket lifetime; only the isolated engine receives the private credential.</summary>
public sealed class SpaceWebSocketRelay : IDisposable
{
    private readonly SemaphoreSlim _nodeSlots = new(128, 128);
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _deviceSlots = new(StringComparer.Ordinal);

    public async Task RelayAsync(HttpContext context, SpaceRuntimePool.Runtime runtime, AuthorizedDevice device,
        string relative, CancellationToken cancellationToken)
    {
        if (!await _nodeSlots.WaitAsync(0, cancellationToken))
        { context.Response.StatusCode = 429; context.Response.Headers.RetryAfter = "2"; return; }
        var admitted = false;
        try
        {
            lock (_gate)
            {
                var count = _deviceSlots.GetValueOrDefault(device.GrantId);
                if (count < 10) { _deviceSlots[device.GrantId] = count + 1; admitted = true; }
            }
            if (!admitted) { context.Response.StatusCode = 429; return; }
            using var upstream = new ClientWebSocket();
            upstream.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                using var cert = new X509Certificate2(certificate);
                return CryptographicOperations.FixedTimeEquals(cert.PublicKey.ExportSubjectPublicKeyInfo(), runtime.Pin);
            };
            upstream.Options.SetRequestHeader("Authorization", "Bearer " + runtime.Secret);
            upstream.Options.SetRequestHeader("X-Picer-Agent-Grant", device.GrantId);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(15));
                await upstream.ConnectAsync(new UriBuilder(new Uri(runtime.Client.BaseAddress!, relative)) { Scheme = "wss" }.Uri, connect.Token);
            }
            using var downstream = await context.WebSockets.AcceptWebSocketAsync();
            var send = CopyAsync(downstream, upstream, lifetime.Token);
            var receive = CopyAsync(upstream, downstream, lifetime.Token);
            try { await Task.WhenAny(send, receive); }
            finally
            {
                await lifetime.CancelAsync(); upstream.Abort(); downstream.Abort();
                try { await Task.WhenAll(send, receive); }
                catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or InvalidDataException) { }
            }
        }
        catch (WebSocketException)
        {
            if (context.Response.HasStarted) context.Abort(); else context.Response.StatusCode = 503;
        }
        finally
        {
            if (admitted) lock (_gate)
            {
                var count = _deviceSlots[device.GrantId] - 1;
                if (count == 0) _deviceSlots.Remove(device.GrantId); else _deviceSlots[device.GrantId] = count;
            }
            _nodeSlots.Release();
        }
    }

    private static async Task CopyAsync(WebSocket from, WebSocket to, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        var messageBytes = 0;
        while (!token.IsCancellationRequested)
        {
            var result = await from.ReceiveAsync(buffer.AsMemory(), token);
            if (result.MessageType == WebSocketMessageType.Close) return;
            messageBytes += result.Count;
            if (messageBytes > 16 * 1024 * 1024) throw new InvalidDataException("socket_message_too_large");
            await to.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, token);
            if (result.EndOfMessage) messageBytes = 0;
        }
    }

    public void Dispose() => _nodeSlots.Dispose();
}
