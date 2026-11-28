using System.Collections.Concurrent;
using System.Net.WebSockets;
using Picer.Core.Identity;

namespace Picer.IndexService.Identity;

/// <summary>Pairs outbound sockets for one GUI and one connector. No index runtime is opened.</summary>
public sealed class IndexRelayDataSessions
{
    private sealed record Pair(string Gui, string Service)
    {
        public TaskCompletionSource<WebSocket> GuiSocket { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<WebSocket> ServiceSocket { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Closed { get; } = new();
        public int ServiceJoined;
    }

    private readonly ConcurrentDictionary<(string Space, string Id), Pair> _pairs = new();

    public RelaySessionInvitation[] Pending(AuthorizedDevice service) => _pairs
        .Where(p => p.Key.Space == service.SpaceId && p.Value.Service == service.DeviceId
            && Volatile.Read(ref p.Value.ServiceJoined) == 0)
        .Select(p => new RelaySessionInvitation(p.Key.Id, p.Value.Gui)).ToArray();

    public async Task ConnectAsync(HttpContext context, AuthorizedDevice device, string session, string peer, CancellationToken token)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        var key = (device.SpaceId, session);
        var gui = device.Kind == "gui";
        Pair pair;
        if (gui)
        {
            pair = new(device.DeviceId, peer);
            if (!_pairs.TryAdd(key, pair)) { context.Response.StatusCode = 409; return; }
        }
        else
        {
            if (!_pairs.TryGetValue(key, out var found) || found.Gui != peer || found.Service != device.DeviceId)
            { context.Response.StatusCode = 404; return; }
            pair = found;
            if (Interlocked.Exchange(ref pair.ServiceJoined, 1) != 0) { context.Response.StatusCode = 409; return; }
        }
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, pair.Closed.Token);
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var wire = new RelayWireSocket(socket);
            (gui ? pair.GuiSocket : pair.ServiceSocket).SetResult(socket);
            var other = await (gui ? pair.ServiceSocket : pair.GuiSocket).Task.WaitAsync(TimeSpan.FromSeconds(45), lifetime.Token);
            using var destination = new RelayWireSocket(other);
            var first = true;
            while (true)
            {
                var message = await wire.ReceiveAsync(lifetime.Token);
                var valid = first
                    ? message.KeyExchange is { } offer && offer.SpaceId == device.SpaceId && offer.SenderDeviceId == device.DeviceId
                        && offer.InitiatorDeviceId == pair.Gui && offer.ResponderDeviceId == pair.Service
                    : message.EncryptedFrame is { } frame && frame.SpaceId == device.SpaceId && frame.SenderDeviceId == device.DeviceId
                        && frame.RecipientDeviceId == peer;
                if (!valid || message.SessionId != session) throw new InvalidDataException("relay_session_binding_invalid");
                await destination.SendAsync(message, lifetime.Token);
                first = false;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or TimeoutException
            or InvalidDataException or System.Text.Json.JsonException or ObjectDisposedException) { }
        finally
        {
            pair.Closed.Cancel();
            ((ICollection<KeyValuePair<(string, string), Pair>>)_pairs).Remove(new(key, pair));
        }
    }
}
