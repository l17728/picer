using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Picer.Core.Identity;

public sealed record RelayKeyExchange(int Version, string SessionId, string SpaceId, string InitiatorDeviceId,
    string ResponderDeviceId, string SenderDeviceId, string EphemeralPublicKey, string Nonce,
    DateTimeOffset CreatedUtc, DateTimeOffset ExpiresUtc, string Signature);

public sealed record RelayEncryptedFrame(int Version, string SessionId, string SpaceId, string SenderDeviceId,
    string RecipientDeviceId, long Sequence, string RequestId, string IdempotencyKey, string Kind,
    DateTimeOffset ExpiresUtc, byte[] Nonce, byte[] Ciphertext, byte[] Tag);

internal sealed record RelayFrameMetadata(int Version, string SessionId, string SpaceId, string SenderDeviceId,
    string RecipientDeviceId, long Sequence, string RequestId, string IdempotencyKey, string Kind,
    DateTimeOffset ExpiresUtc);

/// <summary>Signed ephemeral ECDH offer. The cloud relay may route the offer but cannot derive the session keys.</summary>
public sealed class RelayKeyExchangeOffer : IDisposable
{
    private readonly ECDiffieHellman _ephemeralKey;
    private readonly string _identityPublicKey;
    private bool _disposed;
    public RelayKeyExchange Share { get; }

    private RelayKeyExchangeOffer(RelayKeyExchange share, ECDiffieHellman ephemeralKey, string identityPublicKey)
    { Share = share; _ephemeralKey = ephemeralKey; _identityPublicKey = identityPublicKey; }

    public static RelayKeyExchangeOffer Create(string spaceId, string sessionId, string initiatorDeviceId,
        string responderDeviceId, string senderDeviceId, ECDsa identityKey, DateTimeOffset? now = null)
    {
        ValidateBinding(spaceId, initiatorDeviceId, responderDeviceId);
        if (!Guid.TryParseExact(sessionId, "N", out _) || senderDeviceId != initiatorDeviceId && senderDeviceId != responderDeviceId)
            throw new ArgumentException("relay_sender_not_in_session", nameof(senderDeviceId));
        using var validatedIdentityKey = DeviceAuthorization.ReadPublicKey(DeviceAuthorization.ExportPublicKey(identityKey));
        var created = now ?? DateTimeOffset.UtcNow;
        var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var unsigned = new RelayKeyExchange(1, sessionId, spaceId, initiatorDeviceId,
                responderDeviceId, senderDeviceId, Convert.ToBase64String(ephemeral.ExportSubjectPublicKeyInfo()),
                DeviceAuthorization.Base64Url(RandomNumberGenerator.GetBytes(32)), created, created.AddSeconds(90), "");
            var input = RelaySessionCrypto.ExchangeSigningBytes(unsigned);
            var signature = DeviceAuthorization.Base64Url(identityKey.SignData(input, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            return new RelayKeyExchangeOffer(unsigned with { Signature = signature }, ephemeral,
                DeviceAuthorization.ExportPublicKey(identityKey));
        }
        catch { ephemeral.Dispose(); throw; }
    }

    public RelaySessionCryptoContext AcceptPeer(RelayKeyExchange peer, string expectedPeerSigningKey,
        DateTimeOffset? now = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RelaySessionCrypto.ValidateExchangePair(Share, peer, _identityPublicKey, expectedPeerSigningKey,
            now ?? DateTimeOffset.UtcNow);
        using var peerEphemeral = ECDiffieHellman.Create();
        var peerPublicBytes = Convert.FromBase64String(peer.EphemeralPublicKey);
        peerEphemeral.ImportSubjectPublicKeyInfo(peerPublicBytes, out var read);
        if (read != peerPublicBytes.Length || peerEphemeral.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            throw new InvalidDataException("relay_peer_ephemeral_key_invalid");
        var localPublicBytes = _ephemeralKey.ExportSubjectPublicKeyInfo();
        var localShareBytes = Convert.FromBase64String(Share.EphemeralPublicKey);
        if (!CryptographicOperations.FixedTimeEquals(localPublicBytes, localShareBytes))
            throw new InvalidDataException("relay_local_ephemeral_key_mismatch");
        var secret = _ephemeralKey.DeriveKeyMaterial(peerEphemeral.PublicKey);
        try { return RelaySessionCrypto.CreateContext(Share, peer, secret); }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ephemeralKey.Dispose();
    }

    private static void ValidateBinding(string spaceId, string initiatorDeviceId, string responderDeviceId)
    {
        if (!Guid.TryParseExact(spaceId, "N", out _) || !Guid.TryParseExact(initiatorDeviceId, "N", out _) ||
            !Guid.TryParseExact(responderDeviceId, "N", out _) || initiatorDeviceId == responderDeviceId)
            throw new ArgumentException("relay_session_binding_invalid");
    }
}

/// <summary>End-to-end directional AEAD keys and bounded request-frame encryption for one relay session.</summary>
public sealed class RelaySessionCryptoContext : IDisposable
{
    public const int MaximumPayloadBytes = 2 * 1024 * 1024;
    private readonly byte[] _sendKey;
    private readonly byte[] _receiveKey;
    private long _sendSequence;
    private bool _disposed;

    internal RelaySessionCryptoContext(string sessionId, string spaceId, string localDeviceId,
        string peerDeviceId, byte[] initiatorKey, byte[] responderKey, bool localIsInitiator)
    {
        SessionId = sessionId; SpaceId = spaceId; LocalDeviceId = localDeviceId; PeerDeviceId = peerDeviceId;
        _sendKey = localIsInitiator ? initiatorKey : responderKey;
        _receiveKey = localIsInitiator ? responderKey : initiatorKey;
    }

    public string SessionId { get; }
    public string SpaceId { get; }
    public string LocalDeviceId { get; }
    public string PeerDeviceId { get; }

    public RelayEncryptedFrame Encrypt(string kind, Guid requestId, Guid idempotencyKey, ReadOnlySpan<byte> payload,
        DateTimeOffset? now = null, TimeSpan? lifetime = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (kind is not ("request" or "response" or "chunk" or "cancel" or "error"))
            throw new ArgumentException("relay_frame_kind_invalid", nameof(kind));
        if (requestId == Guid.Empty || idempotencyKey == Guid.Empty || payload.Length > MaximumPayloadBytes)
            throw new ArgumentException("relay_frame_metadata_or_size_invalid");
        var created = now ?? DateTimeOffset.UtcNow;
        var validity = lifetime ?? TimeSpan.FromMinutes(2);
        if (validity <= TimeSpan.Zero || validity > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        var sequence = Interlocked.Increment(ref _sendSequence);
        var metadata = new RelayFrameMetadata(1, SessionId, SpaceId, LocalDeviceId, PeerDeviceId, sequence,
            requestId.ToString("N"), idempotencyKey.ToString("N"), kind, created.Add(validity));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[payload.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_sendKey, tag.Length);
        aes.Encrypt(nonce, payload, ciphertext, tag, JsonSerializer.SerializeToUtf8Bytes(metadata, DeviceAuthorization.Json));
        return new RelayEncryptedFrame(metadata.Version, metadata.SessionId, metadata.SpaceId, metadata.SenderDeviceId,
            metadata.RecipientDeviceId, metadata.Sequence, metadata.RequestId, metadata.IdempotencyKey, metadata.Kind,
            metadata.ExpiresUtc, nonce, ciphertext, tag);
    }

    public byte[] Decrypt(RelayEncryptedFrame frame, RelayReplayWindow replayWindow, DateTimeOffset? now = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(replayWindow);
        var current = now ?? DateTimeOffset.UtcNow;
        ValidateFrame(frame, current);
        var metadata = ToMetadata(frame);
        var plaintext = new byte[frame.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(_receiveKey, 16);
            aes.Decrypt(frame.Nonce, frame.Ciphertext, frame.Tag, plaintext,
                JsonSerializer.SerializeToUtf8Bytes(metadata, DeviceAuthorization.Json));
            if (!replayWindow.TryAccept(frame.Sequence)) throw new InvalidDataException("relay_frame_replayed_or_out_of_window");
            return plaintext;
        }
        catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
    }

    private void ValidateFrame(RelayEncryptedFrame frame, DateTimeOffset now)
    {
        if (frame.Version != 1 || frame.SessionId != SessionId || frame.SpaceId != SpaceId ||
            frame.SenderDeviceId != PeerDeviceId || frame.RecipientDeviceId != LocalDeviceId || frame.Sequence <= 0 ||
            frame.Kind is not ("request" or "response" or "chunk" or "cancel" or "error") ||
            !Guid.TryParseExact(frame.RequestId, "N", out var requestId) || requestId == Guid.Empty ||
            !Guid.TryParseExact(frame.IdempotencyKey, "N", out var operationId) || operationId == Guid.Empty ||
            frame.Nonce is not { Length: 12 } || frame.Tag is not { Length: 16 } ||
            frame.Ciphertext is null || frame.Ciphertext.Length > MaximumPayloadBytes ||
            frame.ExpiresUtc <= now || frame.ExpiresUtc > now.AddMinutes(5))
            throw new InvalidDataException("relay_frame_invalid_or_expired");
    }

    private static RelayFrameMetadata ToMetadata(RelayEncryptedFrame frame) => new(frame.Version, frame.SessionId,
        frame.SpaceId, frame.SenderDeviceId, frame.RecipientDeviceId, frame.Sequence, frame.RequestId,
        frame.IdempotencyKey, frame.Kind, frame.ExpiresUtc);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_sendKey);
        CryptographicOperations.ZeroMemory(_receiveKey);
    }
}

/// <summary>Thread-safe sliding replay window. Sequence order may vary across concurrent multiplexed streams.</summary>
public sealed class RelayReplayWindow
{
    private readonly int _width;
    private readonly object _gate = new();
    private readonly HashSet<long> _seen = [];
    private long _highest;

    public RelayReplayWindow(int width = 4096)
    {
        if (width is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(width));
        _width = width;
    }

    public bool TryAccept(long sequence)
    {
        if (sequence <= 0) return false;
        lock (_gate)
        {
            var floor = Math.Max(1, _highest - _width + 1);
            if (sequence < floor || !_seen.Add(sequence)) return false;
            if (sequence > _highest)
            {
                _highest = sequence;
                var newFloor = Math.Max(1, _highest - _width + 1);
                _seen.RemoveWhere(value => value < newFloor);
            }
            return true;
        }
    }
}

public static class RelaySessionCrypto
{
    internal static byte[] ExchangeSigningBytes(RelayKeyExchange value) =>
        JsonSerializer.SerializeToUtf8Bytes(value with { Signature = "" }, DeviceAuthorization.Json);

    internal static void ValidateExchangePair(RelayKeyExchange local, RelayKeyExchange peer,
        string expectedLocalSigningKey, string expectedPeerSigningKey, DateTimeOffset now)
    {
        var localSender = local.SenderDeviceId;
        if (local.Version != 1 || peer.Version != 1 || local.SessionId != peer.SessionId ||
            local.SpaceId != peer.SpaceId || local.InitiatorDeviceId != peer.InitiatorDeviceId ||
            local.ResponderDeviceId != peer.ResponderDeviceId || local.SenderDeviceId == peer.SenderDeviceId ||
            localSender != local.InitiatorDeviceId && localSender != local.ResponderDeviceId ||
            peer.SenderDeviceId != (localSender == local.InitiatorDeviceId ? local.ResponderDeviceId : local.InitiatorDeviceId) ||
            !Guid.TryParseExact(local.SessionId, "N", out _) || !Guid.TryParseExact(local.SpaceId, "N", out _) ||
            !Guid.TryParseExact(local.InitiatorDeviceId, "N", out _) || !Guid.TryParseExact(local.ResponderDeviceId, "N", out _) ||
            !Guid.TryParseExact(peer.SenderDeviceId, "N", out _) || local.CreatedUtc > now.AddSeconds(30) ||
            peer.CreatedUtc > now.AddSeconds(30) || local.ExpiresUtc <= now || peer.ExpiresUtc <= now ||
            local.ExpiresUtc > local.CreatedUtc.AddMinutes(2) || peer.ExpiresUtc > peer.CreatedUtc.AddMinutes(2))
            throw new InvalidDataException("relay_key_exchange_binding_invalid");
        if (local.ExpiresUtc <= local.CreatedUtc || peer.ExpiresUtc <= peer.CreatedUtc)
            throw new InvalidDataException("relay_key_exchange_expiry_invalid");

        ValidatePublicShare(local);
        ValidatePublicShare(peer);
        using var localSigningKey = DeviceAuthorization.ReadPublicKey(expectedLocalSigningKey);
        var localSignature = DeviceAuthorization.DecodeBase64Url(local.Signature);
        if (!localSigningKey.VerifyData(ExchangeSigningBytes(local), localSignature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new InvalidDataException("relay_local_key_exchange_signature_invalid");
        using var signingKey = DeviceAuthorization.ReadPublicKey(expectedPeerSigningKey);
        var signature = DeviceAuthorization.DecodeBase64Url(peer.Signature);
        if (!signingKey.VerifyData(ExchangeSigningBytes(peer), signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new InvalidDataException("relay_key_exchange_signature_invalid");
    }

    internal static RelaySessionCryptoContext CreateContext(RelayKeyExchange local, RelayKeyExchange peer, byte[] secret)
    {
        var initiator = local.SenderDeviceId == local.InitiatorDeviceId ? local : peer;
        var responder = local.SenderDeviceId == local.ResponderDeviceId ? local : peer;
        var transcript = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1, local.SessionId, local.SpaceId, local.InitiatorDeviceId, local.ResponderDeviceId,
            initiator = new { initiator.EphemeralPublicKey, initiator.Nonce, initiator.Signature },
            responder = new { responder.EphemeralPublicKey, responder.Nonce, responder.Signature }
        }, DeviceAuthorization.Json);
        var salt = SHA256.HashData(transcript);
        var keys = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 64, salt, Encoding.UTF8.GetBytes("Picer.IndexRelay.v1/session"));
        var initiatorKey = keys[..32];
        var responderKey = keys[32..];
        CryptographicOperations.ZeroMemory(keys);
        CryptographicOperations.ZeroMemory(salt);
        CryptographicOperations.ZeroMemory(transcript);
        return new RelaySessionCryptoContext(local.SessionId, local.SpaceId, local.SenderDeviceId,
            peer.SenderDeviceId, initiatorKey, responderKey, local.SenderDeviceId == local.InitiatorDeviceId);
    }

    private static void ValidatePublicShare(RelayKeyExchange share)
    {
        if (DeviceAuthorization.DecodeBase64Url(share.Nonce).Length != 32)
            throw new InvalidDataException("relay_key_exchange_nonce_invalid");
        var bytes = Convert.FromBase64String(share.EphemeralPublicKey);
        using var key = ECDiffieHellman.Create();
        key.ImportSubjectPublicKeyInfo(bytes, out var read);
        if (read != bytes.Length || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            throw new InvalidDataException("relay_peer_ephemeral_key_invalid");
    }
}
