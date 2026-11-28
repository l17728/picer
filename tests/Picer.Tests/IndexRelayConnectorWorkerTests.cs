using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Picer.Core.Identity;
using Picer.Core.Imaging;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Thumbnails;
using Picer.Core.Video;
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
        var accountService = AccountServiceHost.Build(Path.Combine(root, "account"), origin, port, enableRelayData: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        IndexRelayConnectorWorker? worker = null;
        IndexServiceInstance? sourceService = null;
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

            using var db = new PicerDatabase(Path.Combine(root, "local-index", "picer.db"));
            using var thumbs = new ThumbnailService(Path.Combine(root, "thumbs"),
                cipher: new Picer.Core.Security.MediaFileCipher(RandomNumberGenerator.GetBytes(32)));
            var source = db.AddSource(root);
            var photoPath = Path.Combine(root, "one.jpg");
            using (var image = new RasterImage(640, 320, new RgbPixel(120, 30, 50))) image.SaveAsJpeg(photoPath);
            foreach (var file in new[] { "one.jpg", "two.jpg" })
                db.UpsertImage(new ImageRecord { SourceId = source.Id, Path = Path.Combine(root, file), FileSize = 123,
                    ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow, Status = ImageStatus.Active });
            var handlers = new IndexApiHandlers(db, thumbs, new VideoRepository(db));
            sourceService = IndexServiceHost.Build(Path.Combine(root, "local-index"), port: 0, bearerToken: "picer-test-token");
            var sourceUrl = await IndexServiceHost.StartAsync(sourceService.App);
            await using var sourceAgent = await MediaStreamingTests.TestAgent.StartAsync(root, sourceUrl, ServiceSecurity.ComputeSpkiPin(sourceService.Certificate));
            handlers.AgentPhotoPreview = sourceService.Handlers.AgentPhotoPreview;
            handlers.SetLocalMediaGateway(sourceService.Handlers.LocalMediaGateway!);
            worker = new IndexRelayConnectorWorker(store, TimeSpan.FromMilliseconds(100), handlers);
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

            async Task<DeviceAccountClient> PairGuiAsync(bool media = false)
            {
                var request = Guid.NewGuid().ToString("N");
                var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var client = new DeviceAccountClient(new(origin, discovery.ServiceId, pin, discovery.SigningKey, "test", request, "GUI", "gui"), key, _ => { });
                try
                {
                    accountService.Accounts.CreateEnrollment(new(request, DeviceAuthorization.ExportPublicKey(key), "GUI", "gui"));
                    var approved = accountService.Accounts.ApproveEnrollment(account, request, space.Id, origin, pin, serverSigningKey, false, media);
                    await client.ImportAsync(DeviceAuthorization.WriteFileDocument(approved), deadline.Token);
                    return client;
                }
                catch { client.Dispose(); throw; }
            }
            using var gui = await PairGuiAsync();
            using var secondGui = await PairGuiAsync(media: true);
            var target = Assert.Single(await gui.ListRelayConnectorsAsync(deadline.Token));
            using (var gateway = await RelayIndexHttpHandler.ConnectAsync(gui, target.DeviceId, deadline.Token))
            {
                Assert.Equal(handlers.Health(), await gateway.GetHealthAsync(deadline.Token));
                Assert.Equal(await handlers.ListDirectoriesAsync(deadline.Token), await gateway.ListDirectoriesAsync(deadline.Token));
                Assert.Equal(handlers.ListTags().Select(t => t.Id), (await gateway.ListTagsAsync(deadline.Token)).Select(t => t.Id));
                Assert.Equal(handlers.ListAlbums().Select(a => a.Id), (await gateway.ListAlbumsAsync(deadline.Token)).Select(a => a.Id));
                Assert.NotNull(await gateway.GetBrowseMetadataAsync(deadline.Token));
                Assert.Empty((await gateway.SearchVideosAsync(new(), cancellationToken: deadline.Token)).Items);
                Assert.Equal(Assert.Single(handlers.ListSources()).Path, Assert.Single(await gateway.ListSourcesAsync(deadline.Token)).Path);
                var filters = new SearchFilters();
                var direct = await handlers.SearchAsync(new(filters, 2, null), deadline.Token);
                using var secondGateway = await RelayIndexHttpHandler.ConnectAsync(secondGui, target.DeviceId, deadline.Token);
                Assert.NotEqual(gateway.MediaCacheScope, secondGateway.MediaCacheScope);
                var photo = direct.Items.Single(i => i.Path == photoPath);
                var cache = thumbs.GetCachePath(photo.Id);
                Assert.False(File.Exists(cache));
                using var firstThumb = await gateway.OpenThumbnailAsync(photo.Id, deadline.Token);
                Assert.NotNull(firstThumb);
                using var firstBytes = new MemoryStream();
                await firstThumb.CopyToAsync(firstBytes, deadline.Token);
                firstBytes.Position = 0;
                using var image = RasterImage.Load(firstBytes);
                Assert.Equal(256, image.Width);
                Assert.Equal(128, image.Height);
                var cached = await File.ReadAllBytesAsync(cache, deadline.Token);
                Assert.False(cached.SequenceEqual(firstBytes.ToArray()));
                using (File.Open(photoPath, FileMode.Open, FileAccess.Read, FileShare.None))
                using (var repeated = await secondGateway.OpenThumbnailAsync(photo.Id, deadline.Token))
                using (var copy = new MemoryStream())
                {
                    Assert.NotNull(repeated);
                    await repeated.CopyToAsync(copy, deadline.Token);
                    Assert.Equal(firstBytes.ToArray(), copy.ToArray());
                    Assert.Equal(cached, await File.ReadAllBytesAsync(cache, deadline.Token));
                }
                Assert.Null(await gateway.OpenThumbnailAsync(long.MaxValue, deadline.Token));
                var denied = await Assert.ThrowsAsync<HttpRequestException>(() => gateway.OpenPreviewAsync(photo.Id, deadline.Token));
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                Assert.Null(await secondGateway.OpenPreviewAsync(long.MaxValue, deadline.Token));
                Assert.Equal(0, sourceAgent.PreviewRequests);
                using var preview = await secondGateway.OpenPreviewAsync(photo.Id, deadline.Token);
                Assert.NotNull(preview);
                using var fullPreview = RasterImage.Load(preview);
                Assert.True(fullPreview.Width > image.Width);
                Assert.Equal(1, sourceAgent.PreviewRequests);
                var forbidden = await Assert.ThrowsAsync<HttpRequestException>(() => gateway.GetMediaInfoAsync(photo.Id, false, deadline.Token));
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
                var info = await secondGateway.GetMediaInfoAsync(photo.Id, false, deadline.Token);
                Assert.True(info.SupportsStreaming);
                using (var independent = await RelayIndexHttpHandler.ConnectAsync(secondGui, target.DeviceId, deadline.Token))
                {
                    await Assert.ThrowsAsync<InvalidDataException>(() => independent.OpenMediaRangeAsync(photo.Id, false, info, 0, 1, deadline.Token));
                }
                var original = await File.ReadAllBytesAsync(photoPath, deadline.Token);
                Assert.Equal(original.LongLength, info.Length);
                var half = original.Length / 2;
                using var assembled = new MemoryStream();
                foreach (var (offset, count) in new[] { (0, half), (half, original.Length - half) })
                {
                    using var block = await secondGateway.OpenMediaRangeAsync(photo.Id, false, info, offset, count, deadline.Token);
                    await block.CopyToAsync(assembled, deadline.Token);
                }
                Assert.Equal(original, assembled.ToArray());
                await secondGateway.ManageMediaLeaseAsync(info.ETag, true, deadline.Token);
                await Assert.ThrowsAsync<InvalidDataException>(() => secondGateway.OpenMediaRangeAsync(photo.Id, false, info, 0, 1, deadline.Token));
                var concurrent = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
                    i % 2 == 0 ? gateway.SearchAsync(filters, 2, cancellationToken: deadline.Token)
                        : secondGateway.SearchAsync(filters, 1, cancellationToken: deadline.Token)));
                for (var i = 0; i < concurrent.Length; i++)
                    Assert.Equal(direct.Items.Take(i % 2 == 0 ? 2 : 1).Select(item => item.Path), concurrent[i].Items.Select(item => item.Path));
                var first = await gateway.SearchAsync(filters, 1, cancellationToken: deadline.Token);
                Assert.NotNull(first.NextCursor);
                var next = await gateway.SearchAsync(filters, 1, first.NextCursor, deadline.Token);
                Assert.Equal(2, direct.Items.Count);
                Assert.Equal(direct.Items.Select(i => i.Path), first.Items.Concat(next.Items).Select(i => i.Path));
            }

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
            if (sourceService is not null)
            {
                await sourceService.App.StopAsync();
                await sourceService.App.DisposeAsync();
                sourceService.Certificate.Dispose();
                sourceService.Handlers.DisposeDatabase();
            }
            await accountService.App.StopAsync();
            await accountService.App.DisposeAsync();
            accountService.Certificate.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
