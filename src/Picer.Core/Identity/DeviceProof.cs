using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Picer.Core.Identity;

/// <summary>ES256 DPoP proof construction; business retry IDs are deliberately independent of proof IDs.</summary>
public static class DeviceProof
{
    public sealed record Validated(string KeyThumbprint, string PublicKey, string Id, DateTimeOffset IssuedUtc);

    public static string Create(ECDsa key, string method, Uri uri, DateTimeOffset now, string? accessToken = null, string? nonce = null)
    {
        var parameters = key.ExportParameters(false);
        var header = JsonSerializer.SerializeToUtf8Bytes(new { typ = "dpop+jwt", alg = "ES256", jwk = new
        { kty = "EC", crv = "P-256", x = DeviceAuthorization.Base64Url(parameters.Q.X!), y = DeviceAuthorization.Base64Url(parameters.Q.Y!) } });
        var payload = new Dictionary<string, object> { ["jti"] = Guid.NewGuid().ToString("N"), ["htm"] = method.ToUpperInvariant(),
            ["htu"] = uri.GetLeftPart(UriPartial.Path), ["iat"] = now.ToUnixTimeSeconds() };
        if (accessToken is not null) payload["ath"] = DeviceAuthorization.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)));
        if (nonce is not null) payload["nonce"] = nonce;
        var signed = DeviceAuthorization.Base64Url(header) + "." + DeviceAuthorization.Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        return signed + "." + DeviceAuthorization.Base64Url(key.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    public static Validated Validate(string proof, string method, Uri uri, DateTimeOffset now, string? accessToken = null, string? requiredNonce = null)
    {
        try { return ValidateCore(proof, method, uri, now, accessToken, requiredNonce); }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or
            KeyNotFoundException or InvalidOperationException or CryptographicException)
        { throw new InvalidDataException("invalid_dpop", exception); }
    }

    private static Validated ValidateCore(string proof, string method, Uri uri, DateTimeOffset now, string? accessToken, string? requiredNonce)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (proof.Length > 4096) throw new InvalidDataException("invalid_dpop");
        var parts = proof.Split('.');
        if (parts.Length != 3) throw new InvalidDataException("invalid_dpop");
        var header = DeviceAuthorization.ReadDocument<JsonElement>(DeviceAuthorization.DecodeBase64Url(parts[0]));
        var payload = DeviceAuthorization.ReadDocument<JsonElement>(DeviceAuthorization.DecodeBase64Url(parts[1]));
        if (header.GetProperty("typ").GetString() != "dpop+jwt" || header.GetProperty("alg").GetString() != "ES256" ||
            header.TryGetProperty("jku", out _) || header.TryGetProperty("x5u", out _) || header.TryGetProperty("crit", out _))
            throw new InvalidDataException("invalid_dpop");
        var jwk = header.GetProperty("jwk");
        if (jwk.GetProperty("kty").GetString() != "EC" || jwk.GetProperty("crv").GetString() != "P-256" || jwk.TryGetProperty("d", out _))
            throw new InvalidDataException("invalid_dpop_key");
        var x = jwk.GetProperty("x").GetString()!; var y = jwk.GetProperty("y").GetString()!;
        var q = new ECPoint { X = DeviceAuthorization.DecodeBase64Url(x), Y = DeviceAuthorization.DecodeBase64Url(y) };
        if (q.X.Length != 32 || q.Y.Length != 32) throw new InvalidDataException("invalid_dpop_key");
        using var key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = q });
        if (!key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), DeviceAuthorization.DecodeBase64Url(parts[2]),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw new InvalidDataException("invalid_dpop_signature");
        var id = payload.GetProperty("jti").GetString();
        var issued = DateTimeOffset.FromUnixTimeSeconds(payload.GetProperty("iat").GetInt64());
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || Math.Abs((now - issued).TotalSeconds) > 60 ||
            payload.GetProperty("htm").GetString() != method.ToUpperInvariant() || payload.GetProperty("htu").GetString() != uri.GetLeftPart(UriPartial.Path))
            throw new InvalidDataException("invalid_dpop_target");
        if (requiredNonce is not null && (!payload.TryGetProperty("nonce", out var nonce) || nonce.GetString() != requiredNonce))
            throw new InvalidDataException("use_dpop_nonce");
        if (accessToken is not null && (!payload.TryGetProperty("ath", out var ath) ||
            ath.GetString() != DeviceAuthorization.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)))))
            throw new InvalidDataException("invalid_dpop_token");
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new { crv = "P-256", kty = "EC", x, y });
        return new(DeviceAuthorization.Base64Url(SHA256.HashData(canonical)), DeviceAuthorization.ExportPublicKey(key), id, issued);
    }
}
