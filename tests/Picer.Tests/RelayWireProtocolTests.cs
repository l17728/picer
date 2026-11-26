using System.Security.Cryptography;
using Picer.Core.Identity;

namespace Picer.Tests;

public sealed class RelayWireProtocolTests
{
    [Fact]
    public void KeyExchangeAndEncryptedFramesRoundTripThroughBoundedWireEnvelope()
    {
        using var identity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var space = Guid.NewGuid().ToString("N");
        var initiator = Guid.NewGuid().ToString("N");
        var responder = Guid.NewGuid().ToString("N");
        var offer = RelayKeyExchangeOffer.Create(space, Guid.NewGuid().ToString("N"), initiator, responder, initiator, identity);
        using (offer)
        {
            var exchange = RelayWireProtocol.Decode(RelayWireProtocol.Encode(RelayWireProtocol.ForKeyExchange(offer.Share)));
            Assert.Equal(RelayWireProtocol.KeyExchangeType, exchange.Type);
            Assert.Equal(offer.Share.SessionId, exchange.KeyExchange!.SessionId);

            var frame = new RelayEncryptedFrame(1, offer.Share.SessionId, space, initiator, responder, 1,
                Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "request", DateTimeOffset.UtcNow.AddMinutes(1),
                RandomNumberGenerator.GetBytes(12), RandomNumberGenerator.GetBytes(128), RandomNumberGenerator.GetBytes(16));
            var decoded = RelayWireProtocol.Decode(RelayWireProtocol.Encode(RelayWireProtocol.ForEncryptedFrame(frame)));
            Assert.Equal(RelayWireProtocol.EncryptedFrameType, decoded.Type);
            Assert.Equal(frame.SessionId, decoded.EncryptedFrame!.SessionId);
            Assert.Equal(frame.Ciphertext, decoded.EncryptedFrame.Ciphertext);
        }
    }

    [Fact]
    public void RejectsMismatchedHeadersOversizedFramesAndUnknownWireTypes()
    {
        var session = Guid.NewGuid().ToString("N");
        var space = Guid.NewGuid().ToString("N");
        var sender = Guid.NewGuid().ToString("N");
        var recipient = Guid.NewGuid().ToString("N");
        var frame = new RelayEncryptedFrame(1, session, space, sender, recipient, 1,
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "request", DateTimeOffset.UtcNow.AddMinutes(1),
            new byte[12], new byte[32], new byte[16]);

        Assert.Throws<InvalidDataException>(() => RelayWireProtocol.Encode(
            RelayWireProtocol.ForEncryptedFrame(frame) with { SessionId = Guid.NewGuid().ToString("N") }));
        Assert.Throws<InvalidDataException>(() => RelayWireProtocol.Encode(
            RelayWireProtocol.ForEncryptedFrame(frame with { Ciphertext = new byte[RelaySessionCryptoContext.MaximumPayloadBytes + 1] })));
        Assert.Throws<InvalidDataException>(() => RelayWireProtocol.Encode(
            new RelayWireMessage(1, "unknown", session)));
        Assert.Throws<InvalidDataException>(() => RelayWireProtocol.Decode(new byte[RelayWireProtocol.MaximumWireMessageBytes + 1]));
    }
}
