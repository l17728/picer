using System.IO;
using System.Net;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Windows.Controls;
using Picer.App;
using Picer.Core.Gateway;

namespace Picer.E2E;

public sealed class ServiceConnectionSetupTests
{
    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    public void RuntimePreflightUsesActualServiceModeNotWindowConstructor(bool serviceMode, bool configured, bool expected)
    {
        Assert.Equal(expected, MainWindow.ShouldCheckLocalRuntime(serviceMode, configured));
    }

    [Fact]
    public void FirstConnectionWindowHasBlankEndpointAndSecretsWithoutWritingConfiguration()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-pairing-ui-" + Guid.NewGuid().ToString("N"));
        var oldRoot = Environment.GetEnvironmentVariable("PICER_BOOTSTRAP_ROOT");
        Exception? failure = null;
        try
        {
            Environment.SetEnvironmentVariable("PICER_BOOTSTRAP_ROOT", root);
            var thread = new Thread(() =>
            {
                try
                {
                    var window = new Picer.App.Views.BackendSettingsWindow();
                    Assert.Equal("", ((TextBox)window.FindName("IpBox")).Text);
                    Assert.Equal("", ((PasswordBox)window.FindName("TokenBox")).Password);
                    Assert.Equal("", ((TextBox)window.FindName("PinBox")).Text);
                    Assert.True(((RadioButton)window.FindName("RemoteModeBox")).IsChecked);
                    Assert.False(File.Exists(AppPaths.BackendConfigPath));
                    Assert.False(File.Exists(AppPaths.ServiceEndpointPath));
                    window.Close();
                }
                catch (Exception error) { failure = error; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally { Environment.SetEnvironmentVariable("PICER_BOOTSTRAP_ROOT", oldRoot); }
    }

    [Theory]
    [InlineData("http://example.invalid:8445")]
    [InlineData("https://user:secret@example.invalid:8445")]
    [InlineData("https://example.invalid:8445/api")]
    [InlineData("https://example.invalid:8445?token=secret")]
    public void PairingRejectsUnsafeOrNonRootEndpoint(string address)
    {
        Assert.Throws<ArgumentException>(() => ServiceConnectionSetup.Parse(address, "synthetic-token", Convert.ToBase64String(new byte[32])));
    }

    [Fact]
    public void PairingRequiresTokenAndDerSpkiRatherThanSha256Digest()
    {
        Assert.Throws<ArgumentException>(() => ServiceConnectionSetup.Parse("https://example.invalid:8445", "", Convert.ToBase64String(new byte[32])));
        Assert.Throws<ArgumentException>(() => ServiceConnectionSetup.Parse("https://example.invalid:8445", "synthetic-token", "invalid"));
        Assert.Throws<ArgumentException>(() => ServiceConnectionSetup.Parse("https://example.invalid:8445", "synthetic-token", Convert.ToBase64String(new byte[16])));
        Assert.Throws<ArgumentException>(() => ServiceConnectionSetup.Parse("https://example.invalid:8445", "synthetic-token", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
    }

    [Fact]
    public void PairingSavesUserEncryptedTokenAndLoadsWithoutEnvironmentOrJsonEditing()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-pairing-" + Guid.NewGuid().ToString("N"));
        var oldToken = Environment.GetEnvironmentVariable("PICER_INDEX_SERVICE_TOKEN");
        var oldPin = Environment.GetEnvironmentVariable("PICER_INDEX_SERVICE_SPKI_PIN");
        var oldRoot = Environment.GetEnvironmentVariable("PICER_BOOTSTRAP_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("PICER_INDEX_SERVICE_TOKEN", null);
            Environment.SetEnvironmentVariable("PICER_INDEX_SERVICE_SPKI_PIN", null);
            Environment.SetEnvironmentVariable("PICER_BOOTSTRAP_ROOT", root);
            var backend = Path.Combine(root, "backend.json");
            var endpoint = Path.Combine(root, "service-endpoint.json");
            using var key = RSA.Create(2048);
            var pin = key.ExportSubjectPublicKeyInfo();
            ServiceConnectionSetup.Parse("https://example.invalid:8445/", "synthetic-secret-never-cleartext", Convert.ToBase64String(pin)).Save(backend, endpoint);
            var saved = File.ReadAllText(endpoint);
            Assert.Contains("dpapi:", saved);
            Assert.DoesNotContain("synthetic-secret-never-cleartext", saved);
            using (var json = System.Text.Json.JsonDocument.Parse(saved))
            {
                Assert.Equal("https://example.invalid:8445", json.RootElement.GetProperty("Url").GetString());
                Assert.Equal(Convert.ToBase64String(pin), json.RootElement.GetProperty("SpkiPin").GetString());
                Assert.StartsWith("dpapi:", json.RootElement.GetProperty("TokenCipher").GetString());
            }
            var loaded = IndexBackendConfig.Load(backend, endpoint, value => ProtectedData.Unprotect(value, null, DataProtectionScope.CurrentUser));
            Assert.Equal("service", loaded.Mode);
            Assert.Equal("https://example.invalid:8445", loaded.BaseUrl);
            Assert.Equal("synthetic-secret-never-cleartext", loaded.Token);
            Assert.Equal(pin, loaded.SpkiPin);
            var paired = IndexBackendConfig.LoadConfiguredServiceEndpoint(endpoint, value => ProtectedData.Unprotect(value, null, DataProtectionScope.CurrentUser));
            Assert.NotNull(paired);
            Assert.Equal(pin, paired.SpkiPin);
            Assert.Equal(loaded.Token, paired.Token);
            // Exercise the separate case-sensitive consumer used by the bundled agent.
            Assert.Equal(loaded.Token, MediaAgentController.ResolveToken());
            Assert.Equal(Convert.ToBase64String(pin), MediaAgentController.ResolveSpkiPin());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PICER_INDEX_SERVICE_TOKEN", oldToken);
            Environment.SetEnvironmentVariable("PICER_INDEX_SERVICE_SPKI_PIN", oldPin);
            Environment.SetEnvironmentVariable("PICER_BOOTSTRAP_ROOT", oldRoot);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RealTlsDiscoverySendsNoCredentialsAndSavedRawSpkiWorksWithHttpGateway()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var requests = new ConcurrentQueue<(string Method, string Path, string Authorization)>();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(certificate)));
        await using var server = builder.Build();
        server.Use(async (context, next) =>
        {
            requests.Enqueue((context.Request.Method, context.Request.Path.Value ?? "", context.Request.Headers.Authorization.ToString()));
            await next(context);
        });
        server.MapGet("/health", () => Results.Json(new GatewayHealth("ok", 45, "1", "test")));
        var root = Path.Combine(Path.GetTempPath(), "picer-tls-pairing-" + Guid.NewGuid().ToString("N"));
        var oldToken = Environment.GetEnvironmentVariable("PICER_INDEX_SERVICE_TOKEN");
        try
        {
            await server.StartAsync(deadline.Token);
            var url = Assert.Single(server.Urls);
            Environment.SetEnvironmentVariable("PICER_INDEX_SERVICE_TOKEN", null);
            var pin = await MediaAgentSettings.DiscoverSpkiAsync(url, deadline.Token);
            Assert.Equal(HttpIndexGateway.ComputeSpkiPin(certificate), pin);
            Assert.NotEqual(32, pin.Length);
            Assert.Empty(requests); // Discovery performs TLS only, not even a credential-free HTTP request.
            using (var gateway = new HttpIndexGateway(url, "synthetic-pairing-token", pin))
                Assert.Equal("ok", (await gateway.GetHealthAsync(deadline.Token)).Status);
            var endpoint = Path.Combine(root, "service-endpoint.json");
            ServiceConnectionSetup.Parse(url, "synthetic-pairing-token", Convert.ToBase64String(pin)).Save(Path.Combine(root, "backend.json"), endpoint);
            var loaded = IndexBackendConfig.LoadConfiguredServiceEndpoint(endpoint, cipher => ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser))!;
            using (var gateway = new HttpIndexGateway(loaded.BaseUrl!, loaded.Token!, loaded.SpkiPin))
                Assert.Equal(45, (await gateway.GetHealthAsync(deadline.Token)).SchemaVersion);
            Assert.Equal(2, requests.Count);
            Assert.All(requests, captured =>
            {
                Assert.Equal("GET", captured.Method);
                Assert.Equal("/health", captured.Path);
                Assert.Equal("Bearer synthetic-pairing-token", captured.Authorization);
            });
            Assert.Equal(pin, loaded.SpkiPin);
            Assert.Equal("synthetic-pairing-token", loaded.Token);
        }
        finally
        {
            deadline.Cancel();
            Environment.SetEnvironmentVariable("PICER_INDEX_SERVICE_TOKEN", oldToken);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
