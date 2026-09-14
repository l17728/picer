using System.Security.Cryptography;
using System.Text.Json;

namespace Picer.ModelServer;

public sealed record ArtifactSigningInfo(string Algorithm, string PublicKey, string KeyId);

public sealed class ArtifactSigner : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public ArtifactSigningInfo GetInfo()
    {
        var publicKey = _key.ExportSubjectPublicKeyInfo();
        var keyId = Convert.ToHexString(SHA256.HashData(publicKey))[..16].ToLowerInvariant();
        return new("ecdsa-p256-sha256", Convert.ToBase64String(publicKey), keyId);
    }

    public TrainingArtifact Sign(TrainingArtifact artifact)
    {
        var unsigned = artifact with { Sha256 = null, Signature = null, SignatureAlgorithm = null, SigningKeyId = null };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(unsigned, _json);
        var hash = SHA256.HashData(bytes);
        var info = GetInfo();
        return unsigned with
        {
            Sha256 = Convert.ToHexString(hash).ToLowerInvariant(),
            Signature = Convert.ToBase64String(_key.SignHash(hash)),
            SignatureAlgorithm = info.Algorithm,
            SigningKeyId = info.KeyId
        };
    }

    public void Dispose() => _key.Dispose();
}
