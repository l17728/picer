using System.Security.Cryptography;
using System.Text.Json;

namespace Picer.ModelServer;

public sealed record ArtifactSigningInfo(string Algorithm, string PublicKey, string KeyId);

public sealed class ArtifactSigner : IDisposable
{
    private readonly ECDsa _key;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public ArtifactSigner()
    {
        _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var root = Environment.GetEnvironmentVariable("PICER_TRAINING_ARCHIVE_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "training-archive");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "artifact-signing-key.pem");
        try
        {
            if (File.Exists(path)) _key.ImportFromPem(File.ReadAllText(path));
            else File.WriteAllText(path, _key.ExportPkcs8PrivateKeyPem());
        }
        catch
        {
            // A malformed key must not prevent the platform from starting;
            // retain the newly generated key and persist it for future runs.
            File.WriteAllText(path, _key.ExportPkcs8PrivateKeyPem());
        }
    }

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
