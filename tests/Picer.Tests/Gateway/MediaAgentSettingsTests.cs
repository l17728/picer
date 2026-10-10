using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Picer.Core.Gateway;

namespace Picer.Tests.Gateway;

public sealed class MediaAgentSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-agent-settings-" + Guid.NewGuid().ToString("N"));
    private readonly string _pin = CreatePin();
    private static string CreatePin() { using var key = RSA.Create(2048); return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()); }
    public MediaAgentSettingsTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void EncryptedSaveRoundTripsWithoutPlaintextToken()
    {
        var path = Path.Combine(_root, "settings.json");
        var key = RandomNumberGenerator.GetBytes(32);
        byte[] Crypt(byte[] value) => value.Select((b, i) => (byte)(b ^ key[i % key.Length])).ToArray();
        var settings = new MediaAgentSettings("https://localhost:8445", "synthetic-secret", _pin, [_root]);
        settings.Save(path, Crypt);
        Assert.DoesNotContain(settings.Token, File.ReadAllText(path));
        var restored = MediaAgentSettings.Load(path, Crypt)!;
        Assert.Equal(settings.Token, restored.Token);
        Assert.Equal(settings.Roots, restored.Roots);
        Assert.Equal(settings.SpkiPin, restored.SpkiPin);
    }

    [Fact]
    public void EnvironmentConnectionRequiresItsOwnTokenPinAndExplicitRoots()
    {
        var environment = new Dictionary<string, string> { ["PICER_INDEX_SERVICE_IP"] = "127.0.0.1", ["PICER_INDEX_SERVICE_TOKEN"] = "synthetic", ["PICER_DATA_ROOT"] = _root };
        string? Get(string key) => environment.GetValueOrDefault(key);
        Assert.ThrowsAny<ArgumentException>(() => MediaAgentSettings.FromEnvironment(Get));
        environment["PICER_INDEX_SERVICE_SPKI_PIN"] = _pin;
        Assert.Throws<ArgumentException>(() => MediaAgentSettings.FromEnvironment(Get));
        environment["PICER_MEDIA_AGENT_ROOTS"] = _root;
        Assert.Equal("https://127.0.0.1:8445", MediaAgentSettings.FromEnvironment(Get)!.ServiceUrl);
    }

    [Theory]
    [InlineData("http://localhost:8445")]
    [InlineData("https://secret@localhost:8445")]
    [InlineData("https://localhost:8445/api")]
    public void InsecureOrNonRootAddressesAreRejected(string address) => Assert.Throws<ArgumentException>(() => MediaAgentSettings.NormalizeServiceUrl(address));

    [Theory]
    [InlineData("localhost:9445", "https://localhost:9445")]
    [InlineData("127.0.0.1", "https://127.0.0.1:8445")]
    [InlineData("::1", "https://[::1]:8445")]
    public void ServiceAddressPreservesExplicitPortsAndIpv6(string address, string expected) => Assert.Equal(expected, MediaAgentSettings.NormalizeServiceUrl(address));

    [Fact]
    public void InvalidRemoteFfmpegFallsBackOnlyToCompleteBundledPair()
    {
        var tools = Path.Combine(_root, "tools", "ffmpeg"); Directory.CreateDirectory(tools);
        var executable = Path.Combine(tools, "ffmpeg.exe"); File.WriteAllText(executable, "synthetic");
        Assert.Empty(MediaAgentSettings.ResolveFfmpeg("/remote-only/ffmpeg", _root));
        File.WriteAllText(Path.Combine(tools, "ffprobe.exe"), "synthetic");
        Assert.Equal(executable, MediaAgentSettings.ResolveFfmpeg("/remote-only/ffmpeg", _root));
    }

    [Fact]
    public void CertificatePinIsMandatoryAndCannotMatchAnotherKey()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=synthetic", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.False(MediaAgentSettings.MatchesCertificate(certificate, null));
        Assert.False(MediaAgentSettings.MatchesCertificate(certificate, new byte[40]));
        Assert.True(MediaAgentSettings.MatchesCertificate(certificate, HttpIndexGateway.ComputeSpkiPin(certificate)));
        Assert.Equal(HttpIndexGateway.ComputeSpkiPin(certificate), MediaAgentSettings.ValidateSpkiPin(Convert.ToBase64String(HttpIndexGateway.ComputeSpkiPin(certificate))));
        Assert.Throws<ArgumentException>(() => MediaAgentSettings.ValidateSpkiPin(Convert.ToBase64String(SHA256.HashData(HttpIndexGateway.ComputeSpkiPin(certificate)))));
    }

    [Fact]
    public async Task PairingDiscoverySendsNoHttpRequestOrBearerToken()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Windows Schannel cannot authenticate a server with an ephemeral key.
        // Importing PFX without PersistKeySet creates a disposable key container.
        using var certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx));
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var tls = new System.Net.Security.SslStream(client.GetStream());
            await tls.AuthenticateAsServerAsync(new System.Net.Security.SslServerAuthenticationOptions { ServerCertificate = certificate }, timeout.Token);
            var applicationByte = new byte[] { 0xff };
            try { return await tls.ReadAsync(applicationByte, timeout.Token); }
            catch (IOException exception) when (exception.InnerException is System.Net.Sockets.SocketException
                { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionReset })
            {
                // Schannel can reset a closing TLS 1.3 connection with unread session
                // tickets even after close_notify. No application byte was observed.
                // Other I/O failures and any received HTTP byte still fail this test.
                Assert.Equal(0xff, applicationByte[0]);
                return 0;
            }
        });
        var address = "https://localhost:" + ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        byte[] discovered;
        try { discovered = await MediaAgentSettings.DiscoverSpkiAsync(address, timeout.Token); }
        catch { await server; throw; }
        Assert.Equal(HttpIndexGateway.ComputeSpkiPin(certificate), discovered);
        Assert.Equal(0, await server);
    }

    [Fact]
    public void CorruptSettingsAreReportedWithoutOverwritingOriginalFile()
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, "{bad-json");
        Assert.Throws<System.Text.Json.JsonException>(() => MediaAgentSettings.Load(path, bytes => bytes));
        Assert.Equal("{bad-json", File.ReadAllText(path));
    }
}
