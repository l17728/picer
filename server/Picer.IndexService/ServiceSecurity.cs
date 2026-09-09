using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Picer.Core.Logging;

namespace Picer.IndexService;

[SupportedOSPlatform("windows")]
public static class ServiceSecurity
{
    private sealed record TokenFile(string TokenCipher);
    private sealed record EndpointFile(string Url, string SpkiPin, string TokenCipher, string PipeName);

    public static string ResolveToken(string dataRoot)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("PICER_INDEX_SERVICE_TOKEN");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment;
        var path = Path.Combine(dataRoot, "service-token.json");
        if (File.Exists(path))
        {
            var file = JsonSerializer.Deserialize<TokenFile>(File.ReadAllText(path));
            if (file is not null)
                return Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(file.TokenCipher)));
        }
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var payload = JsonSerializer.Serialize(new TokenFile(Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(token)))));
        File.WriteAllText(path, payload);
        return token;
    }

    public static X509Certificate2 EnsureHttpsCertificate(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "https-cert.pfx.enc");
        if (Environment.GetEnvironmentVariable("PICER_INDEX_SERVICE_RENEW_CERT") == "1" && File.Exists(path))
        {
            File.Delete(path);
            AppLogger.Info("检测到 PICER_INDEX_SERVICE_RENEW_CERT=1，已轮换服务证书（客户端需重新确认 SPKI 指纹）。");
        }
        if (File.Exists(path)) return new X509Certificate2(Unprotect(File.ReadAllBytes(path)));
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=picer-index-service", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        var exported = certificate.Export(X509ContentType.Pkcs12);
        File.WriteAllBytes(path, Protect(exported));
        return new X509Certificate2(exported);
    }

    public static byte[] ComputeSpkiPin(X509Certificate2 certificate) => certificate.PublicKey.ExportSubjectPublicKeyInfo();

    public static void WriteEndpointFile(string dataRoot, string baseUrl, byte[] spkiPin, string token, string pipeName)
    {
        var payload = JsonSerializer.Serialize(new EndpointFile(
            baseUrl,
            Convert.ToBase64String(spkiPin),
            Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(token))),
            pipeName));
        File.WriteAllText(Path.Combine(dataRoot, "service-endpoint.json"), payload);
    }

    private static byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);

    private static byte[] Unprotect(byte[] cipher) => ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
}
