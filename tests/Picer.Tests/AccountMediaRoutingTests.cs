using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Identity;
using Picer.Core.Models;
using Picer.IndexService;
using Picer.IndexService.Identity;
using Xunit;

namespace Picer.Tests;

public sealed class AccountMediaRoutingTests
{
    [Fact]
    public async Task MediaUsesBoundSourceDespiteCallerAgentOverrideAndRevocationClosesSocket()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-account-media-" + Guid.NewGuid().ToString("N"));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var origin = $"https://127.0.0.1:{port}";
        var instance = AccountServiceHost.Build(Path.Combine(root, "service"), origin, port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await IndexServiceHost.StartAsync(instance.App);
            var account = instance.Accounts.Register("media-owner", "media-owner-password-123!", "多机空间");
            var space = Assert.Single(instance.Accounts.ListSpaces(account));
            using var signing = IdentitySigningKey.LoadOrCreate(instance.Accounts.Root, instance.Accounts.ServiceId);
            var publicKeys = new Dictionary<string, string>();
            async Task<DeviceAccountClient> Pair(string kind)
            {
                var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var requestId = Guid.NewGuid().ToString("N");
                instance.Accounts.CreateEnrollment(new(requestId, DeviceAuthorization.ExportPublicKey(key), kind, kind));
                var pin = Convert.ToBase64String(ServiceSecurity.ComputeSpkiPin(instance.Certificate));
                var document = instance.Accounts.ApproveEnrollment(account, requestId, space.Id, origin, pin, signing, true, true);
                var connection = new DeviceConnection(origin, instance.Accounts.ServiceId, pin, DeviceAuthorization.ExportPublicKey(signing), "test", requestId, kind, kind);
                var client = new DeviceAccountClient(connection, key, value => connection = value);
                await client.ImportAsync(DeviceAuthorization.WriteFileDocument(document), deadline.Token);
                publicKeys.Add(client.Connection.Session!.GrantId, DeviceAuthorization.ExportPublicKey(key));
                return client;
            }
            using var gui = await Pair("gui");
            using var first = await Pair("agent");
            using var second = await Pair("agent");
            using var firstSocket = await first.ConnectWebSocketAsync("api/agent/v1/connect", deadline.Token);
            using var secondSocket = await second.ConnectWebSocketAsync("api/agent/v1/connect", deadline.Token);
            foreach (var socket in new[] { firstSocket, secondSocket })
            {
                await MediaWire.SendJsonAsync(socket, new { type = "agent.register", agentId = "untrusted-machine-name", capabilities = new[] { MediaWire.Capability } }, deadline.Token);
                var registered = await MediaWire.ReceiveJsonAsync<JsonElement>(socket, deadline.Token);
                Assert.Equal("agent.registered", registered.GetProperty("type").GetString());
            }
            var mediaRoot = Path.Combine(root, "media"); Directory.CreateDirectory(mediaRoot);
            var path = Path.Combine(mediaRoot, "photo.jpg");
            var bytes = RandomNumberGenerator.GetBytes(4096); await File.WriteAllBytesAsync(path, bytes, deadline.Token);
            SpacePublisher publisher; long imageId;
            using (var database = new PicerDatabase(Path.Combine(instance.Accounts.SpaceRoot(space.Id), "picer.db")))
            {
                var session = gui.Connection.Session!;
                var device = instance.Accounts.Authorize(session.AccessToken, publicKeys[session.GrantId], space.Id)!;
                var receiver = new SpaceSyncReceiver(database, device);
                publisher = receiver.Register(new(Guid.NewGuid().ToString("N"), device.DeviceId));
                var image = new ImageRecord { Id = 1, SourceId = 1, Path = path, FileSize = bytes.Length,
                    ModifiedUtc = File.GetLastWriteTimeUtc(path), IndexedUtc = DateTime.UtcNow,
                    Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
                Assert.Equal(200, receiver.Apply(new(publisher.PublisherId, publisher.Epoch, 1, Guid.NewGuid().ToString("N"), "images",
                    JsonSerializer.SerializeToElement(new SpaceImageBatch(1, mediaRoot, [image]), DeviceAuthorization.Json),
                    [new("image", 1, Guid.NewGuid().ToString("N"))])).Status);
                imageId = Assert.Single(database.GetImages(includeMissing: true)).Id;
            }
            using (var binding = await gui.SpaceClient.PostAsJsonAsync("api/sync/v2/source-bindings",
                       new SpaceSourceBindingRequest(publisher.PublisherId, first.Connection.Session!.GrantId, [1]), deadline.Token))
                binding.EnsureSuccessStatusCode();
            var serving = ServeOneAsync();
            var unexpected = MediaWire.ReceiveJsonAsync<JsonElement>(secondSocket, deadline.Token);
            var downloaded = await gui.SpaceClient.GetByteArrayAsync($"api/gateway/v1/assets/images/{imageId}?agentId={second.Connection.Session!.GrantId}", deadline.Token);
            Assert.Equal(bytes, downloaded);
            Assert.False(unexpected.IsCompleted);
            await serving;
            instance.Accounts.Revoke(account, second.Connection.Session!.GrantId);
            await Assert.ThrowsAnyAsync<Exception>(async () => await unexpected.WaitAsync(TimeSpan.FromSeconds(12)));
            Assert.NotEqual(WebSocketState.Open, secondSocket.State);

            async Task ServeOneAsync()
            {
                JsonElement command;
                do { command = await MediaWire.ReceiveJsonAsync<JsonElement>(firstSocket, deadline.Token); }
                while (command.GetProperty("type").GetString() == "agent.config");
                Assert.Equal("media.open", command.GetProperty("type").GetString());
                Assert.Equal(path, command.GetProperty("path").GetString());
                using var media = await first.ConnectWebSocketAsync("api/agent/v1/media/" + command.GetProperty("requestId").GetString(), deadline.Token);
                try { await MediaFileSession.ServeAsync(media, path, [mediaRoot], 0, "image/jpeg", new MediaPreviewCache(), _ => { }, deadline.Token); }
                catch (Exception exception) when (exception is WebSocketException or IOException) { }
            }

        }
        finally
        {
            await deadline.CancelAsync();
            await instance.App.StopAsync(); await instance.App.DisposeAsync(); instance.Certificate.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
