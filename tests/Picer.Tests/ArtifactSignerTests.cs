using System.Security.Cryptography;
using System.Text.Json;
using Picer.ModelServer;

namespace Picer.Tests;

public sealed class ArtifactSignerTests
{
    [Fact]
    public void Sign_ProducesVerifiableP256Signature()
    {
        using var signer = new ArtifactSigner();
        var artifact = new TrainingArtifact("artifact-1", "prototype-calibration", "sface-v1",
            DateTime.UnixEpoch, [new PersonPrototype(1, [1f, 0f], .5, 2)],
            new TrainingMetrics(2, 1, 1, 1, 0, 0, 1, 0));

        var signed = signer.Sign(artifact);
        var unsigned = signed with { Sha256 = null, Signature = null, SignatureAlgorithm = null, SigningKeyId = null };
        var payload = JsonSerializer.SerializeToUtf8Bytes(unsigned, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var hash = SHA256.HashData(payload);
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(signer.GetInfo().PublicKey), out _);

        Assert.Equal(Convert.ToHexString(hash).ToLowerInvariant(), signed.Sha256);
        Assert.Equal("ecdsa-p256-sha256", signed.SignatureAlgorithm);
        Assert.True(verifier.VerifyHash(hash, Convert.FromBase64String(signed.Signature!)));
    }
}
