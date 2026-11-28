using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task IndexServiceConnectorUsesDedicatedRelayGrantAndHeartbeatSocket()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-index-relay-connector-" + Guid.NewGuid().ToString("N"));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var origin = $"https://127.0.0.1:{port}";
        var instance = AccountServiceHost.Build(Path.Combine(root, "service"), origin, port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await IndexServiceHost.StartAsync(instance.App);
            var account = instance.Accounts.Register("relay-owner", "relay-owner-password-123!", "Relay 空间");
            var space = Assert.Single(instance.Accounts.ListSpaces(account));
            using var signing = IdentitySigningKey.LoadOrCreate(instance.Accounts.Root, instance.Accounts.ServiceId);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var requestId = Guid.NewGuid().ToString("N");
            instance.Accounts.CreateEnrollment(new(requestId, DeviceAuthorization.ExportPublicKey(key), "本机 IndexService", "index-service"));
            var pin = Convert.ToBase64String(ServiceSecurity.ComputeSpkiPin(instance.Certificate));
            var document = instance.Accounts.ApproveEnrollment(account, requestId, space.Id, origin, pin, signing,
                allowEditing: false, allowMedia: false);
            var connection = new DeviceConnection(origin, instance.Accounts.ServiceId, pin,
                DeviceAuthorization.ExportPublicKey(signing), "test", requestId, "本机索引服务", "index-service");
            using var client = new DeviceAccountClient(connection, key, value => connection = value);
            await client.ImportAsync(DeviceAuthorization.WriteFileDocument(document), deadline.Token);

            using var socket = await client.ConnectWebSocketAsync("api/index/v1/connect", deadline.Token);
            var registry = instance.App.Services.GetRequiredService<IndexRelayConnectorRegistry>();
            var connected = Assert.IsType<IndexRelayConnectorStatus>(registry.GetStatus(client.Connection.Session!.GrantId));
            await RelayConnectorProtocol.SendAsync(socket, RelayConnectorProtocol.Heartbeat, deadline.Token);
            var acknowledgement = await RelayConnectorProtocol.ReceiveAsync(socket, deadline.Token);
            Assert.Equal(RelayConnectorProtocol.HeartbeatAck, acknowledgement.Type);
            Assert.True(registry.GetStatus(client.Connection.Session!.GrantId)!.LastHeartbeatUtc >= connected.LastHeartbeatUtc);

            using var guiKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            async Task<DeviceAccountClient> PairGui(string owner, string spaceId)
            {
                var request = Guid.NewGuid().ToString("N");
                instance.Accounts.CreateEnrollment(new(request, DeviceAuthorization.ExportPublicKey(guiKey), "GUI", "gui"));
                var approval = instance.Accounts.ApproveEnrollment(owner, request, spaceId, origin, pin, signing, false, false);
                var gui = new DeviceAccountClient(new(origin, instance.Accounts.ServiceId, pin,
                    DeviceAuthorization.ExportPublicKey(signing), "test", request, "GUI", "gui"), guiKey, _ => { });
                await gui.ImportAsync(DeviceAuthorization.WriteFileDocument(approval), deadline.Token);
                return gui;
            }
            using var gui = await PairGui(account, space.Id);
            var otherAccount = instance.Accounts.Register("other-relay-owner", "other-relay-password-123!", "其他空间");
            using var outside = await PairGui(otherAccount, Assert.Single(instance.Accounts.ListSpaces(otherAccount)).Id);
            var peer = await gui.ResolveRelayPeerAsync(connected.DeviceId, deadline.Token);
            Assert.Equal(peer, Assert.Single(await gui.ListRelayConnectorsAsync(deadline.Token)));
            Assert.Empty(await outside.ListRelayConnectorsAsync(deadline.Token));
            Assert.Equal(DeviceAuthorization.ExportPublicKey(key), peer.PublicKey);
            var reverse = await client.ResolveRelayPeerAsync(gui.Connection.Session!.DeviceId, deadline.Token);
            Assert.Equal(DeviceAuthorization.ExportPublicKey(guiKey), reverse.PublicKey);
            using var guiOffer = gui.CreateRelayOffer(Guid.NewGuid().ToString("N"), connected.DeviceId);
            using var serviceOffer = client.CreateRelayOffer(guiOffer.Share.SessionId, reverse.DeviceId);
            using var guiSession = await gui.AcceptRelayPeerAsync(guiOffer, serviceOffer.Share, deadline.Token);
            using var serviceSession = await client.AcceptRelayPeerAsync(serviceOffer, guiOffer.Share, deadline.Token);
            var queryFrame = guiSession.Encrypt("request", Guid.NewGuid(), Guid.NewGuid(), "read"u8);
            Assert.Equal("read"u8.ToArray(), serviceSession.Decrypt(queryFrame, new RelayReplayWindow()));
            var outsideDenied = await Assert.ThrowsAsync<HttpRequestException>(() => outside.ResolveRelayPeerAsync(connected.DeviceId, deadline.Token));
            Assert.Equal(HttpStatusCode.NotFound, outsideDenied.StatusCode);

            using var denied = await client.SpaceClient.GetAsync("health", deadline.Token);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.ConnectWebSocketAsync("api/agent/v1/connect", deadline.Token));

            instance.Accounts.Revoke(account, client.Connection.Session.GrantId);
            var revoked = await Assert.ThrowsAsync<HttpRequestException>(() => gui.ResolveRelayPeerAsync(connected.DeviceId, deadline.Token));
            Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
            Assert.Empty(await gui.ListRelayConnectorsAsync(deadline.Token));
            using var revocationDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<WebSocketException>(() => RelayConnectorProtocol.ReceiveAsync(socket, revocationDeadline.Token));
            Assert.Null(registry.GetStatus(client.Connection.Session.GrantId));
        }
        finally
        {
            await instance.App.StopAsync(); await instance.App.DisposeAsync(); instance.Certificate.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task VideoPeopleAnalysisPassesScopeOnlyWithAnalysisGrant()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-account-analysis-" + Guid.NewGuid().ToString("N"));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var origin = $"https://127.0.0.1:{port}";
        var instance = AccountServiceHost.Build(Path.Combine(root, "service"), origin, port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await IndexServiceHost.StartAsync(instance.App);
            var account = instance.Accounts.Register("analysis-owner", "analysis-owner-password-123!", "分析空间");
            var space = Assert.Single(instance.Accounts.ListSpaces(account));
            using var signing = IdentitySigningKey.LoadOrCreate(instance.Accounts.Root, instance.Accounts.ServiceId);
            var pin = Convert.ToBase64String(ServiceSecurity.ComputeSpkiPin(instance.Certificate));
            async Task<DeviceAccountClient> Pair(bool allowAnalysis)
            {
                var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var requestId = Guid.NewGuid().ToString("N");
                instance.Accounts.CreateEnrollment(new(requestId, DeviceAuthorization.ExportPublicKey(key), "gui", "gui"));
                var document = instance.Accounts.ApproveEnrollment(account, requestId, space.Id, origin, pin, signing, true, true, allowAnalysis);
                var connection = new DeviceConnection(origin, instance.Accounts.ServiceId, pin, DeviceAuthorization.ExportPublicKey(signing), "test", requestId, "gui", "gui");
                var client = new DeviceAccountClient(connection, key, value => connection = value);
                await client.ImportAsync(DeviceAuthorization.WriteFileDocument(document), deadline.Token);
                return client;
            }
            using var plain = await Pair(false);
            using var analyst = await Pair(true);
            // Without the grant the account host rejects the route before any handler runs.
            using var denied = await plain.SpaceClient.PostAsync("api/service/v1/video-people/1", content: null, deadline.Token);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            // With the grant the scope check passes; the unknown video yields 404, never 403.
            using var allowed = await analyst.SpaceClient.PostAsync("api/service/v1/video-people/1", content: null, deadline.Token);
            Assert.NotEqual(HttpStatusCode.Forbidden, allowed.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, allowed.StatusCode);
        }
        finally
        {
            await instance.App.StopAsync(); await instance.App.DisposeAsync(); instance.Certificate.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

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
            using var mediaLeases = new MediaReadLeasePool();
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
            using (var database = new PicerDatabase(Path.Combine(instance.Accounts.SpaceRoot(space.Id), "picer.db")))
                Assert.Equal(first.Connection.Session!.GrantId, SpaceSourceRouting.ResolveUniqueAgent(database));
            var headServing = ServeOneAsync();
            using (var head = await gui.SpaceClient.SendAsync(new HttpRequestMessage(HttpMethod.Head,
                       $"api/gateway/v1/assets/images/{imageId}"), deadline.Token))
            {
                Assert.Equal(HttpStatusCode.OK, head.StatusCode);
                Assert.Contains("1", head.Headers.GetValues("X-Picer-Media-Streaming"));
                var etag = head.Headers.ETag ?? throw new InvalidDataException("The source response omitted its entity tag.");
                var rangeServing = ServeOneAsync();
                using var request = new HttpRequestMessage(HttpMethod.Get, $"api/gateway/v1/assets/images/{imageId}");
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 63);
                request.Headers.IfMatch.Add(etag);
                using var range = await gui.SpaceClient.SendAsync(request, deadline.Token);
                Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
                Assert.Equal(0L, range.Content.Headers.ContentRange?.From);
                Assert.Equal(63L, range.Content.Headers.ContentRange?.To);
                Assert.Equal(bytes.AsSpan(0, 64).ToArray(), await range.Content.ReadAsByteArrayAsync(deadline.Token));
                await rangeServing;
                using var released = await gui.SpaceClient.PostAsJsonAsync("api/gateway/v1/media-lease",
                    new MediaLeaseRequest(etag.ToString(), true), deadline.Token);
                Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);
                var releaseNotice = await MediaWire.ReceiveJsonAsync<JsonElement>(secondSocket, deadline.Token);
                Assert.Equal("media.release", releaseNotice.GetProperty("type").GetString());
            }
            await headServing;
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
                while (command.GetProperty("type").GetString() is "agent.config" or "media.release" or "media.keepalive");
                Assert.Equal("media.open", command.GetProperty("type").GetString());
                Assert.Equal(path, command.GetProperty("path").GetString());
                var expectedETag = command.TryGetProperty("expectedETag", out var expected) && expected.ValueKind == JsonValueKind.String
                    ? expected.GetString() : null;
                var keepLease = command.TryGetProperty("keepLease", out var keep) && keep.GetBoolean();
                using var media = await first.ConnectWebSocketAsync("api/agent/v1/media/" + command.GetProperty("requestId").GetString(), deadline.Token);
                try { await MediaFileSession.ServeAsync(media, path, [mediaRoot], 0, "image/jpeg", new MediaPreviewCache(), _ => { }, deadline.Token,
                    mediaLeases, expectedETag, keepLease); }
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
