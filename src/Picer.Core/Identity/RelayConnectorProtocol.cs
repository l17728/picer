using System.Net.WebSockets;
using System.Text.Json;

namespace Picer.Core.Identity;

public sealed record RelayConnectorFrame(int Version, string Type, bool InvitationsAvailable = false);

/// <summary>Heartbeat-only control frames for the persistent IndexService-to-Relay socket.</summary>
public static class RelayConnectorProtocol
{
    public const int CurrentVersion = 1;
    public const int MaximumControlMessageBytes = 4096;
    public const string Heartbeat = "index.connector.heartbeat";
    public const string HeartbeatAck = "index.connector.heartbeat.ack";

    public static byte[] Encode(string type) => JsonSerializer.SerializeToUtf8Bytes(new RelayConnectorFrame(CurrentVersion, type));

    public static async Task<RelayConnectorFrame> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var buffer = new byte[1024];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
            if (result.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException("relay_control_frame_must_be_text");
            if (message.Length + result.Count > MaximumControlMessageBytes)
                throw new InvalidDataException("relay_control_frame_too_large");
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        var frame = JsonSerializer.Deserialize<RelayConnectorFrame>(message.ToArray())
            ?? throw new InvalidDataException("relay_control_frame_invalid");
        if (frame.Version != CurrentVersion || string.IsNullOrWhiteSpace(frame.Type))
            throw new InvalidDataException("relay_control_frame_unsupported");
        return frame;
    }

    public static Task SendAsync(WebSocket socket, string type, CancellationToken cancellationToken) =>
        socket.SendAsync(Encode(type).AsMemory(), WebSocketMessageType.Text, true, cancellationToken).AsTask();

    public static Task AcknowledgeInvitationsAsync(WebSocket socket, CancellationToken token) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new RelayConnectorFrame(CurrentVersion, HeartbeatAck, true)).AsMemory(),
            WebSocketMessageType.Text, true, token).AsTask();
}
