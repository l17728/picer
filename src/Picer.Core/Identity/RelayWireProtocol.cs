using System.Buffers;
using System.Net.WebSockets;

namespace Picer.Core.Identity;

public sealed record RelayWireMessage(int Version, string Type, string SessionId,
    RelayKeyExchange? KeyExchange = null, RelayEncryptedFrame? EncryptedFrame = null);

/// <summary>Bounded binary WebSocket transport for signed key shares and opaque encrypted frames.</summary>
public static class RelayWireProtocol
{
    public const int CurrentVersion = 1;
    public const int MaximumWireMessageBytes = 3 * 1024 * 1024;
    public const string KeyExchangeType = "key_exchange";
    public const string EncryptedFrameType = "encrypted_frame";

    public static RelayWireMessage ForKeyExchange(RelayKeyExchange share) =>
        new(CurrentVersion, KeyExchangeType, share.SessionId, share);

    public static RelayWireMessage ForEncryptedFrame(RelayEncryptedFrame frame) =>
        new(CurrentVersion, EncryptedFrameType, frame.SessionId, EncryptedFrame: frame);

    public static byte[] Encode(RelayWireMessage message)
    {
        Validate(message);
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(message, DeviceAuthorization.Json);
        if (bytes.Length > MaximumWireMessageBytes) throw new InvalidDataException("relay_wire_message_too_large");
        return bytes;
    }

    public static RelayWireMessage Decode(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaximumWireMessageBytes) throw new InvalidDataException("relay_wire_message_size_invalid");
        var message = DeviceAuthorization.ReadDocument<RelayWireMessage>(bytes, MaximumWireMessageBytes);
        Validate(message);
        return message;
    }

    private static void Validate(RelayWireMessage message)
    {
        if (message.Version != CurrentVersion || !Guid.TryParseExact(message.SessionId, "N", out _))
            throw new InvalidDataException("relay_wire_header_invalid");
        if (message.Type == KeyExchangeType)
        {
            if (message.KeyExchange is not { } share || message.EncryptedFrame is not null || share.Version != CurrentVersion ||
                share.SessionId != message.SessionId || !Guid.TryParseExact(share.SpaceId, "N", out _) ||
                !Guid.TryParseExact(share.SenderDeviceId, "N", out _) || share.Signature.Length > 512 ||
                share.EphemeralPublicKey.Length > 1024 || share.Nonce.Length > 128)
                throw new InvalidDataException("relay_wire_key_exchange_invalid");
            return;
        }
        if (message.Type == EncryptedFrameType)
        {
            if (message.EncryptedFrame is not { } frame || message.KeyExchange is not null ||
                frame.Version != CurrentVersion || frame.SessionId != message.SessionId ||
                frame.Ciphertext is null || frame.Ciphertext.Length > RelaySessionCryptoContext.MaximumPayloadBytes ||
                frame.Nonce is not { Length: 12 } || frame.Tag is not { Length: 16 } ||
                !Guid.TryParseExact(frame.SpaceId, "N", out _) || !Guid.TryParseExact(frame.SenderDeviceId, "N", out _) ||
                !Guid.TryParseExact(frame.RecipientDeviceId, "N", out _) || frame.Sequence <= 0)
                throw new InvalidDataException("relay_wire_encrypted_frame_invalid");
            return;
        }
        throw new InvalidDataException("relay_wire_type_unsupported");
    }
}

/// <summary>One reader and serialized writes for a single WebSocket peer.</summary>
public sealed class RelayWireSocket(WebSocket socket) : IDisposable
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public async Task SendAsync(RelayWireMessage message, CancellationToken cancellationToken = default)
    {
        var bytes = RelayWireProtocol.Encode(message);
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false); }
        finally { _sendGate.Release(); }
    }

    public async Task<RelayWireMessage> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        using var message = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer.AsMemory(0, 64 * 1024), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
                if (result.MessageType != WebSocketMessageType.Binary)
                    throw new InvalidDataException("relay_wire_requires_binary_messages");
                if (message.Length + result.Count > RelayWireProtocol.MaximumWireMessageBytes)
                    throw new InvalidDataException("relay_wire_message_too_large");
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            return RelayWireProtocol.Decode(message.ToArray());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    public void Dispose() => _sendGate.Dispose();
}
