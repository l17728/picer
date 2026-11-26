using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Picer.Core.Identity;
using Picer.IndexService;
using Picer.IndexService.Identity;

namespace Picer.Tests;

public sealed class IndexRelayConnectorWorkerTests
{
    [Fact]
    public async Task PairedLocalServiceConnectorMaintainsHeartbeatAndShutsDownCleanly()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-relay-worker-" + Guid.NewGuid().ToString("N"));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var origin = $"https://127.0.0.1:{port}";
        var accountService = AccountServiceHost.Build(Path.Combine(root, "account"), origin, port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        IndexRelayConnectorWorker? worker = null;
        try
        {
            await IndexServiceHost.StartAsync(accountService.App);
            var account = accountService.Accounts.Register("relay-worker-owner", "relay-worker-password-123!", "relay-worker-space");
            var space = Assert.Single(accountService.Accounts.ListSpaces(account));
            using var serverSigningKey = IdentitySigningKey.LoadOrCreate(accountService.Accounts.Root, accountService.Accounts.ServiceId);
            var pin = Convert.ToBase64String(ServiceSecurity.ComputeSpkiPin(accountService.Certificate));
            var discovery = new ServiceDiscovery(2, accountService.Accounts.ServiceId, origin, pin,
                DeviceAuthorization.ExportPublicKey(serverSigningKey), origin + "/identity/enrollments", origin + "/identity/service");
            var store = new IndexRelayDeviceCredentialStore(Path.Combine(root, "local-index", "relay.json"));
            store.Create(discovery, pin, "test-local-index-service");
            var enrollment = await store.BeginEnrollmentAsync(deadline.Token);
            var connection = store.Load();
            var authorization = accountService.Accounts.ApproveEnrollment(account, enrollment.RequestId, space.Id,
                origin, pin, serverSigningKey, allowEditing: false, allowMedia: false);
            await store.ImportAuthorizationAsync(DeviceAuthorization.WriteFileDocument(authorization), deadline.Token);
            Assert.Equal("relay.connect", store.Load().Session!.Scope);

            worker = new IndexRelayConnectorWorker(store, TimeSpan.FromMilliseconds(100));
            await worker.StartAsync(deadline.Token);
            var registry = accountService.App.Services.GetRequiredService<IndexRelayConnectorRegistry>();
            while (worker.State.LastHeartbeatUtc is null)
            {
                deadline.Token.ThrowIfCancellationRequested();
                await Task.Delay(50, deadline.Token);
            }

            var grantId = store.Load().Session!.GrantId;
            Assert.Equal(connection.RequestId, store.Load().RequestId);
            Assert.NotNull(registry.GetStatus(grantId));
            Assert.True(worker.State.Connected);
            Assert.True(worker.State.LastHeartbeatUtc <= DateTimeOffset.UtcNow);

            await worker.StopAsync(deadline.Token);
            Assert.False(worker.State.Connected);
            while (registry.GetStatus(grantId) is not null)
            {
                deadline.Token.ThrowIfCancellationRequested();
                await Task.Delay(50, deadline.Token);
            }
            Assert.Null(registry.GetStatus(grantId));
        }
        finally
        {
            if (worker is not null) worker.Dispose();
            await accountService.App.StopAsync();
            await accountService.App.DisposeAsync();
            accountService.Certificate.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
