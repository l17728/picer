using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Picer.IndexService.Identity;

public static class AccountHttpsCertificate
{
    // Schannel server TLS needs a persisted key handle on Windows; ephemeral keys work on OpenSSL hosts.
    private static X509KeyStorageFlags KeyStorage => OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
    public static X509Certificate2 Load(string root, string origin)
    {
        var supplied = Environment.GetEnvironmentVariable("PICER_INDEX_HTTPS_PFX");
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            var certificate = new X509Certificate2(supplied, Environment.GetEnvironmentVariable("PICER_INDEX_HTTPS_PFX_PASSWORD"),
                KeyStorage);
            if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow)
            { certificate.Dispose(); throw new InvalidOperationException("https_certificate_invalid_or_expired"); }
            return certificate;
        }
        // Preserve the installed service identity during account migration and certificate renewal.
        if (File.Exists(Path.Combine(root, "https-cert.pfx")) || File.Exists(Path.Combine(root, "https-cert.pfx.enc")))
            return ServiceSecurity.EnsureHttpsCertificate(root);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Picer IndexService", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        var host = new Uri(origin).Host;
        if (IPAddress.TryParse(host, out var address)) names.AddIpAddress(address); else names.AddDnsName(host);
        if (host != "localhost") names.AddDnsName("localhost");
        if (host != IPAddress.Loopback.ToString()) names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));
        var bytes = generated.Export(X509ContentType.Pkcs12);
        try
        {
            var path = Path.Combine(root, OperatingSystem.IsWindows() ? "https-cert.pfx.enc" : "https-cert.pfx");
            var stored = OperatingSystem.IsWindows() ? ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser) : bytes;
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var output = new FileStream(path, options)) { output.Write(stored); output.Flush(true); }
            return new X509Certificate2(bytes, (string?)null, KeyStorage);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
