using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Picer.Core.Identity;

/// <summary>Public enrollment data; private keys and service credentials never belong in this document.</summary>
public sealed record DeviceEnrollmentRequest(string RequestId, string PublicKey, string DeviceName, string ClientKind);
public sealed record DeviceAuthorizationFile(int Version, string ServiceId, string Origin, string SpkiPin,
    string RequestId, string SpaceId, string AccountId, string Scope, DateTimeOffset ExpiresUtc,
    string RedemptionCode, string PublicKey, string Signature)
{
    public string Issuer { get; init; } = "";
    public long MembershipVersion { get; init; }
    public string RecipientKeyThumbprint { get; init; } = "";
    public string AccountName { get; init; } = "";
    public string SpaceName { get; init; } = "";
}
public sealed record DeviceSession(string ServiceId, string SpaceId, string DeviceId, string GrantId,
    string AccountId, string Scope, string AccessToken, string RefreshToken, DateTimeOffset ExpiresUtc)
{
    public string AccountName { get; init; } = "";
    public string SpaceName { get; init; } = "";
}
public sealed record AccountSpace(string Id, string Name, string Role);

public static class DeviceAuthorization
{
    public const int MaximumFileBytes = 64 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 8 };

    public static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static string Base64Url(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] DecodeBase64Url(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaximumFileBytes || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new FormatException("invalid_base64url");
        var text = value.Replace('-', '+').Replace('_', '/');
        var bytes = Convert.FromBase64String(text.PadRight((text.Length + 3) / 4 * 4, '='));
        if (Base64Url(bytes) != value) throw new FormatException("noncanonical_base64url");
        return bytes;
    }

    public static string NormalizeOrigin(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("请输入 HTTPS 服务根地址，不包含账号、路径或查询参数。");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public static string SpaceAddress(string origin, string spaceId)
    {
        if (!Guid.TryParseExact(spaceId, "N", out _)) throw new ArgumentException("invalid_space");
        return NormalizeOrigin(origin) + "/spaces/" + spaceId;
    }

    public static bool IsSpacePath(string path) => path.Length == 40 && path.StartsWith("/spaces/", StringComparison.Ordinal)
        && Guid.TryParseExact(path[8..], "N", out _);

    public static string ExportPublicKey(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    public static ECDsa ReadPublicKey(string encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (encoded.Length > 256) throw new ArgumentException("invalid_device_key");
        var key = ECDsa.Create();
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            var parameters = key.ExportParameters(false);
            if (read != bytes.Length || parameters.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                throw new ArgumentException("invalid_device_key");
            return key;
        }
        catch { key.Dispose(); throw; }
    }

    public static byte[] SigningBytes(DeviceAuthorizationFile file) => JsonSerializer.SerializeToUtf8Bytes(file with { Signature = "" }, Json);
    public static DeviceAuthorizationFile Sign(DeviceAuthorizationFile file, ECDsa key)
    {
        using var recipient = ReadPublicKey(file.PublicKey);
        file = file with { Issuer = NormalizeOrigin(file.Origin), RecipientKeyThumbprint = KeyThumbprint(recipient) };
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", typ = "picer-auth+jwt", kid = KeyThumbprint(key) }));
        var input = header + "." + Base64Url(SigningBytes(file));
        return file with { Signature = input + "." + Base64Url(key.SignData(Encoding.ASCII.GetBytes(input),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) };
    }

    public static string KeyThumbprint(ECDsa key)
    {
        var parameters = key.ExportParameters(false);
        return Base64Url(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { crv = "P-256", kty = "EC",
            x = Base64Url(parameters.Q.X!), y = Base64Url(parameters.Q.Y!) })));
    }

    public static byte[] WriteFileDocument(DeviceAuthorizationFile file) =>
        JsonSerializer.SerializeToUtf8Bytes(new { version = 1, signedPayload = file.Signature }, Json);

    /// <summary>Parses only. The caller must validate against an independently trusted server key before redemption.</summary>
    public static DeviceAuthorizationFile ReadFileDocument(ReadOnlyMemory<byte> bytes)
    {
        var envelope = ReadDocument<JsonElement>(bytes);
        if (envelope.GetProperty("version").GetInt32() != 1 || envelope.EnumerateObject().Count() != 2)
            throw new InvalidDataException("invalid_authorization_envelope");
        var compact = envelope.GetProperty("signedPayload").GetString() ?? throw new InvalidDataException("missing_jws");
        var parts = compact.Split('.');
        if (parts.Length != 3) throw new InvalidDataException("invalid_jws");
        return ReadDocument<DeviceAuthorizationFile>(DecodeBase64Url(parts[1])) with { Signature = compact };
    }

    private static bool VerifySignature(DeviceAuthorizationFile file, ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(file.Signature);
        var parts = file.Signature.Split('.');
        if (parts.Length != 3) return false;
        var header = ReadDocument<JsonElement>(DecodeBase64Url(parts[0]));
        if (header.EnumerateObject().Count() != 3 || header.GetProperty("alg").GetString() != "ES256" ||
            header.GetProperty("typ").GetString() != "picer-auth+jwt" || header.GetProperty("kid").GetString() != KeyThumbprint(key)) return false;
        return CryptographicOperations.FixedTimeEquals(DecodeBase64Url(parts[1]), SigningBytes(file)) &&
            key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), DecodeBase64Url(parts[2]),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public static void ValidateFile(DeviceAuthorizationFile file, string expectedOrigin, string expectedServiceId,
        string expectedRequestId, string expectedPublicKey, ECDsa trustedSigningKey, DateTimeOffset now)
    {
        using var recipient = ReadPublicKey(expectedPublicKey);
        if (file.Version != 1 || file.Origin != NormalizeOrigin(expectedOrigin) || file.ServiceId != expectedServiceId ||
            file.Issuer != file.Origin || file.MembershipVersion < 1 || file.RecipientKeyThumbprint != KeyThumbprint(recipient) ||
            file.RequestId != expectedRequestId || file.PublicKey != expectedPublicKey || file.ExpiresUtc <= now ||
            file.ExpiresUtc > now.AddMinutes(16) || !Guid.TryParseExact(file.SpaceId, "N", out _) ||
            DecodeBase64Url(file.RedemptionCode).Length != 32 ||
            !VerifySignature(file, trustedSigningKey))
            throw new InvalidDataException("设备授权文件无效、已过期或不属于当前设备。");
    }

    public static T ReadDocument<T>(ReadOnlyMemory<byte> bytes, int maximumBytes = MaximumFileBytes)
    {
        if (bytes.Length == 0 || bytes.Length > maximumBytes) throw new InvalidDataException("authorization_file_size");
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        ValidateUniqueProperties(doc.RootElement);
        return doc.RootElement.Deserialize<T>(Json) ?? throw new InvalidDataException("authorization_file_empty");
    }

    private static void ValidateUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("authorization_duplicate_property");
                ValidateUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) ValidateUniqueProperties(item);
    }
}
