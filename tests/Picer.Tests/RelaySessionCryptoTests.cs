using System.Security.Cryptography;
using System.Text;
using Picer.Core.Identity;

namespace Picer.Tests;

public sealed class RelaySessionCryptoTests
{
    [Fact]
    public void SignedEcdhSessionEncryptsBothDirectionsAndBindsTheFrameMetadata()
    {
        var now = DateTimeOffset.UtcNow;
        var space = Guid.NewGuid().ToString("N");
        var initiator = Guid.NewGuid().ToString("N");
        var responder = Guid.NewGuid().ToString("N");
        var session = Guid.NewGuid().ToString("N");
        using var initiatorIdentity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var responderIdentity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var initiatorOffer = RelayKeyExchangeOffer.Create(space, session, initiator, responder, initiator,
            initiatorIdentity, now);
        using var responderOffer = RelayKeyExchangeOffer.Create(space, session, initiator, responder, responder,
            responderIdentity, now);
        using var initiatorSession = initiatorOffer.AcceptPeer(responderOffer.Share,
            DeviceAuthorization.ExportPublicKey(responderIdentity), now);
        using var responderSession = responderOffer.AcceptPeer(initiatorOffer.Share,
            DeviceAuthorization.ExportPublicKey(initiatorIdentity), now);
        var replay = new RelayReplayWindow();
        var requestId = Guid.NewGuid();
        var idempotencyKey = Guid.NewGuid();
        var request = Encoding.UTF8.GetBytes("GET /api/gateway/v1/search?query=private");

        var requestFrame = initiatorSession.Encrypt("request", requestId, idempotencyKey, request, now);
        Assert.NotEqual(request, requestFrame.Ciphertext);
        Assert.Equal(request, responderSession.Decrypt(requestFrame, replay, now));

        var response = Encoding.UTF8.GetBytes("private search results");
        var responseFrame = responderSession.Encrypt("response", requestId, idempotencyKey, response, now);
        Assert.Equal(response, initiatorSession.Decrypt(responseFrame, new RelayReplayWindow(), now));
        Assert.Throws<InvalidDataException>(() => responderSession.Decrypt(requestFrame, replay, now));
        var tamperedCiphertext = responseFrame.Ciphertext.ToArray();
        tamperedCiphertext[0] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => initiatorSession.Decrypt(
            responseFrame with { Ciphertext = tamperedCiphertext }, new RelayReplayWindow(), now));
    }

    [Fact]
    public void RejectsWrongPeerBindingTamperingExpiredFramesAndReplay()
    {
        var now = DateTimeOffset.UtcNow;
        var space = Guid.NewGuid().ToString("N");
        var initiator = Guid.NewGuid().ToString("N");
        var responder = Guid.NewGuid().ToString("N");
        var session = Guid.NewGuid().ToString("N");
        using var initiatorIdentity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var responderIdentity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var initiatorOffer = RelayKeyExchangeOffer.Create(space, session, initiator, responder, initiator, initiatorIdentity, now);
        using var responderOffer = RelayKeyExchangeOffer.Create(space, session, initiator, responder, responder, responderIdentity, now);
        using var initiatorSession = initiatorOffer.AcceptPeer(responderOffer.Share,
            DeviceAuthorization.ExportPublicKey(responderIdentity), now);
        using var responderSession = responderOffer.AcceptPeer(initiatorOffer.Share,
            DeviceAuthorization.ExportPublicKey(initiatorIdentity), now);

        Assert.Throws<InvalidDataException>(() => initiatorOffer.AcceptPeer(responderOffer.Share with { SpaceId = Guid.NewGuid().ToString("N") },
            DeviceAuthorization.ExportPublicKey(responderIdentity), now));
        using var attackerIdentity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<InvalidDataException>(() => initiatorOffer.AcceptPeer(responderOffer.Share,
            DeviceAuthorization.ExportPublicKey(attackerIdentity), now));

        var frame = initiatorSession.Encrypt("request", Guid.NewGuid(), Guid.NewGuid(), [1, 2, 3], now);
        var replay = new RelayReplayWindow(width: 8);
        var modified = frame with { RecipientDeviceId = Guid.NewGuid().ToString("N") };
        Assert.Throws<InvalidDataException>(() => responderSession.Decrypt(modified, replay, now));
        Assert.Equal(new byte[] { 1, 2, 3 }, responderSession.Decrypt(frame, replay, now));
        Assert.Throws<InvalidDataException>(() => responderSession.Decrypt(frame with { ExpiresUtc = now.AddSeconds(-1) }, replay, now));
        Assert.Throws<InvalidDataException>(() => responderSession.Decrypt(frame, replay, now));
    }

    [Fact]
    public void ReplayWindowAllowsConcurrentReorderingButRejectsOldAndDuplicateSequences()
    {
        var window = new RelayReplayWindow(width: 4);
        Assert.True(window.TryAccept(3));
        Assert.True(window.TryAccept(1));
        Assert.True(window.TryAccept(4));
        Assert.False(window.TryAccept(1));
        Assert.True(window.TryAccept(7));
        Assert.False(window.TryAccept(3));
        Assert.False(window.TryAccept(0));
    }
}
