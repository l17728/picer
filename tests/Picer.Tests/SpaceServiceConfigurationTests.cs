using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Picer.Core.Gateway;
using Picer.Core.Identity;
using Picer.IndexService;
using Picer.IndexService.Identity;

namespace Picer.Tests;

/// <summary>
/// A photo space runs as an isolated engine under its own directory. The model endpoint
/// configuration lives at the service level; agents only advertise face, vision and speech
/// capabilities after the space replays it to them. Without inheritance every agent-based
/// analysis in account mode was reported as "media agent offline".
/// </summary>
public sealed class SpaceServiceConfigurationTests
{
    private const string ServiceEndpoint = "http://127.0.0.1:8091/api/face-inference";
    private const string SpaceEndpoint = "http://127.0.0.1:8092/api/face-inference";

    private static string Configuration(string endpoint) =>
        JsonSerializer.Serialize(new { version = "1", updatedUtc = "2026-01-01T00:00:00Z", config = new { PICER_LOCAL_FACE_ENDPOINT = endpoint } });

    private static async Task<string?> EndpointReplayedToAgent(bool serviceConfiguration, bool spaceConfiguration)
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-space-config-" + Guid.NewGuid().ToString("N"));
        // The service-level configuration lives in the root handed to AccountServiceHost.Build,
        // one level above the account store (which is where photo spaces are nested).
        var serviceRoot = Path.Combine(root, "service");
        Directory.CreateDirectory(serviceRoot);
        if (serviceConfiguration) await File.WriteAllTextAsync(Path.Combine(serviceRoot, "service-config.json"), Configuration(ServiceEndpoint));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var origin = $"https://127.0.0.1:{port}";
        var instance = AccountServiceHost.Build(serviceRoot, origin, port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await IndexServiceHost.StartAsync(instance.App);
            var account = instance.Accounts.Register("config-owner", "config-owner-password-123!", "配置继承空间");
            var space = Assert.Single(instance.Accounts.ListSpaces(account));
            if (spaceConfiguration)
            {
                Directory.CreateDirectory(instance.Accounts.SpaceRoot(space.Id));
                await File.WriteAllTextAsync(Path.Combine(instance.Accounts.SpaceRoot(space.Id), "service-config.json"), Configuration(SpaceEndpoint));
            }
            using var signing = IdentitySigningKey.LoadOrCreate(instance.Accounts.Root, instance.Accounts.ServiceId);
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var requestId = Guid.NewGuid().ToString("N");
            instance.Accounts.CreateEnrollment(new(requestId, DeviceAuthorization.ExportPublicKey(key), "config-agent", "agent"));
            var pin = Convert.ToBase64String(ServiceSecurity.ComputeSpkiPin(instance.Certificate));
            var document = instance.Accounts.ApproveEnrollment(account, requestId, space.Id, origin, pin, signing, true, true);
            var connection = new DeviceConnection(origin, instance.Accounts.ServiceId, pin, DeviceAuthorization.ExportPublicKey(signing), "test", requestId, "config-agent", "agent");
            using var agent = new DeviceAccountClient(connection, key, value => connection = value);
            await agent.ImportAsync(DeviceAuthorization.WriteFileDocument(document), deadline.Token);
            using var socket = await agent.ConnectWebSocketAsync("api/agent/v1/connect", deadline.Token);
            await MediaWire.SendJsonAsync(socket, new { type = "agent.register", agentId = "config-agent", capabilities = new[] { "originalRead" } }, deadline.Token);
            var registered = await MediaWire.ReceiveJsonAsync<JsonElement>(socket, deadline.Token);
            Assert.Equal("agent.registered", registered.GetProperty("type").GetString());
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var replay = await MediaWire.ReceiveJsonAsync<JsonElement>(socket, timeout.Token);
                Assert.Equal("agent.config", replay.GetProperty("type").GetString());
                return replay.GetProperty("config").GetProperty("PICER_LOCAL_FACE_ENDPOINT").GetString();
            }
            catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
            {
                return null;
            }
        }
        finally
        {
            await instance.App.StopAsync(); await instance.App.DisposeAsync(); instance.Certificate.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SpaceInheritsServiceLevelConfigurationForItsAgents()
    {
        var replayed = await EndpointReplayedToAgent(serviceConfiguration: true, spaceConfiguration: false);
        Assert.Equal(ServiceEndpoint, replayed);
    }

    [Fact]
    public async Task SpaceOwnConfigurationOverridesTheServiceLevelOne()
    {
        var replayed = await EndpointReplayedToAgent(serviceConfiguration: true, spaceConfiguration: true);
        Assert.Equal(SpaceEndpoint, replayed);
    }

    [Fact]
    public async Task WithoutAnyConfigurationAgentsRegisterWithoutReplay()
    {
        var replayed = await EndpointReplayedToAgent(serviceConfiguration: false, spaceConfiguration: false);
        Assert.Null(replayed);
    }
}
