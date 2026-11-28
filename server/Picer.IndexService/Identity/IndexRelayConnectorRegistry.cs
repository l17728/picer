using System.Collections.Concurrent;
using System.Net.WebSockets;
using Picer.Core.Identity;

namespace Picer.IndexService.Identity;

public sealed record IndexRelayConnectorStatus(string SpaceId, string DeviceId, string GrantId,
    DateTimeOffset ConnectedAtUtc, DateTimeOffset LastHeartbeatUtc);

/// <summary>Tracks authenticated, outbound IndexService control sockets without creating a tenant runtime.</summary>
public sealed class IndexRelayConnectorRegistry
{
    private sealed record Connector(IndexRelayConnectorStatus Status, WebSocket Socket);
    private readonly ConcurrentDictionary<string, Connector> _connectors = new(StringComparer.Ordinal);

    public IndexRelayConnectorStatus? GetStatus(string grantId) =>
        _connectors.TryGetValue(grantId, out var connector) && connector.Socket.State == WebSocketState.Open
            ? connector.Status : null;

    public IReadOnlyList<string> ConnectedDeviceIds(string spaceId) =>
        _connectors.Values.Where(c => c.Status.SpaceId == spaceId && c.Socket.State == WebSocketState.Open)
            .Select(c => c.Status.DeviceId).Order(StringComparer.Ordinal).ToArray();

    public async Task ConnectAsync(HttpContext context, AuthorizedDevice device, CancellationToken cancellationToken, IndexRelayDataSessions? sessions = null)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var status = new IndexRelayConnectorStatus(device.SpaceId, device.DeviceId, device.GrantId, now, now);
        var connector = new Connector(status, socket);
        if (!_connectors.TryAdd(device.GrantId, connector))
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "connector_already_connected", cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var frame = await RelayConnectorProtocol.ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
                if (frame.Type != RelayConnectorProtocol.Heartbeat)
                    throw new InvalidDataException("relay_control_frame_unexpected");
                var updated = connector with { Status = connector.Status with { LastHeartbeatUtc = DateTimeOffset.UtcNow } };
                _connectors.TryUpdate(device.GrantId, updated, connector);
                connector = updated;
                if (sessions is not null && sessions.Pending(device).Length > 0)
                    await RelayConnectorProtocol.AcknowledgeInvitationsAsync(socket, cancellationToken).ConfigureAwait(false);
                else
                    await RelayConnectorProtocol.SendAsync(socket, RelayConnectorProtocol.HeartbeatAck, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (WebSocketException) { }
        catch (InvalidDataException)
        {
            if (socket.State == WebSocketState.Open)
                try { await socket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "invalid_connector_control_frame", CancellationToken.None).ConfigureAwait(false); }
                catch (WebSocketException) { socket.Abort(); }
        }
        finally
        {
            ((ICollection<KeyValuePair<string, Connector>>)_connectors).Remove(new(device.GrantId, connector));
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "connector_disconnected", CancellationToken.None).ConfigureAwait(false); }
                catch (WebSocketException) { socket.Abort(); }
        }
    }
}
