using System.Security.Cryptography;
using System.Text;
using Picer.Core.Identity;

namespace Picer.Tests;

public sealed class RelaySessionCryptoTests
{
    [Fact]
    public async Task MultiplexerCorrelatesConcurrentEncryptedResponsesWithinBoundedCapacity()
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
        using var initiatorSession = initiatorOffer.AcceptPeer(responderOffer.Share, DeviceAuthorization.ExportPublicKey(responderIdentity), now);
        using var responderSession = responderOffer.AcceptPeer(initiatorOffer.Share, DeviceAuthorization.ExportPublicKey(initiatorIdentity), now);
        var receiverReplay = new RelayReplayWindow();
        var senderReplay = new RelayReplayWindow();
        RelayRequestMultiplexer? multiplexer = null;
        var active = 0;
        var peak = 0;
        multiplexer = new RelayRequestMultiplexer(initiatorSession, receiverReplay, async (requestFrame, token) =>
        {
            var count = Interlocked.Increment(ref active);
            UpdatePeak(ref peak, count);
            try
            {
                await Task.Delay(20, token);
                var request = responderSession.Decrypt(requestFrame, senderReplay);
                var response = responderSession.Encrypt("response", Guid.ParseExact(requestFrame.RequestId, "N"),
                    Guid.ParseExact(requestFrame.IdempotencyKey, "N"), request);
                multiplexer!.Accept(response);
            }
            finally { Interlocked.Decrement(ref active); }
        }, maxInFlight: 3);
        using (multiplexer)
        {
            var requests = Enumerable.Range(0, 12).Select(async value =>
            {
                var payload = Encoding.UTF8.GetBytes("query-" + value);
                var result = await multiplexer.RequestAsync(Guid.NewGuid(), payload);
                Assert.Equal(payload, result);
            });
            await Task.WhenAll(requests);
            Assert.InRange(peak, 1, 3);
            Assert.Equal(0, multiplexer.PendingCount);
        }
    }

    [Fact]
    public async Task MultiplexerTimeoutReleasesItsPendingSlot()
    {
        var now = DateTimeOffset.UtcNow;
        var space = Guid.NewGuid().ToString("N");
        var initiator = Guid.NewGuid().ToString("N");
        var responder = Guid.NewGuid().ToString("N");
        using var initiatorIdentity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var responderIdentity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var initiatorOffer = RelayKeyExchangeOffer.Create(space, Guid.NewGuid().ToString("N"), initiator, responder, initiator, initiatorIdentity, now);
        using var responderOffer = RelayKeyExchangeOffer.Create(space, initiatorOffer.Share.SessionId, initiator, responder, responder, responderIdentity, now);
        using var initiatorSession = initiatorOffer.AcceptPeer(responderOffer.Share, DeviceAuthorization.ExportPublicKey(responderIdentity), now);
        var receiverReplay = new RelayReplayWindow();
        var requestSends = 0;
        var cancelSends = 0;
        using var multiplexer = new RelayRequestMultiplexer(initiatorSession, receiverReplay, (frame, token) =>
        {
            if (frame.Kind == "cancel") { Interlocked.Increment(ref cancelSends); return Task.CompletedTask; }
            Interlocked.Increment(ref requestSends);
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, maxInFlight: 1,
            defaultTimeout: TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(() => multiplexer.RequestAsync(Guid.NewGuid(), new byte[] { 1 }));
        Assert.Equal(0, multiplexer.PendingCount);
        await Assert.ThrowsAsync<TimeoutException>(() => multiplexer.RequestAsync(Guid.NewGuid(), new byte[] { 2 }, timeout: TimeSpan.FromMilliseconds(50)));
        Assert.Equal(2, requestSends);
        Assert.Equal(2, cancelSends);
        Assert.Equal(0, multiplexer.PendingCount);

        using var activeCancellation = new CancellationTokenSource();
        var active = multiplexer.RequestAsync(Guid.NewGuid(), new byte[] { 3 }, activeCancellation.Token, TimeSpan.FromMinutes(1));
        try
        {
            var timeout = await Assert.ThrowsAsync<TimeoutException>(() => multiplexer.RequestAsync(Guid.NewGuid(), new byte[] { 4 })
                .WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None));
            Assert.Equal("relay_request_timed_out", timeout.Message);
            Assert.Equal(1, multiplexer.PendingCount);
            Assert.Equal(3, requestSends);
        }
        finally
        {
            activeCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        }
    }

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

    private static void UpdatePeak(ref int peak, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref peak);
            if (candidate <= current || Interlocked.CompareExchange(ref peak, candidate, current) == current) return;
        }
    }
}
