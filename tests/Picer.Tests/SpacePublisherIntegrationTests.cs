using System.Net;
using System.Security.Cryptography;
using Picer.Core.Data;
using Picer.Core.Identity;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.People;
using Picer.IndexService;
using Picer.IndexService.Identity;
using Xunit;

namespace Picer.Tests;

public sealed class SpacePublisherIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostCommittedResponseResumesDurableBatchAndPublishesDescriptionsAndCatalog(bool restrictScope)
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-publisher-" + Guid.NewGuid().ToString("N"));
        const string origin = "https://picer.test";
        var instance = AccountServiceHost.Build(Path.Combine(root, "service"), origin, 0);
        try
        {
            var address = await IndexServiceHost.StartAsync(instance.App);
            var owner = instance.Accounts.Register("publisher-user", "test-publisher-password!", "照片空间");
            var space = Assert.Single(instance.Accounts.ListSpaces(owner));
            using var signing = IdentitySigningKey.LoadOrCreate(instance.Accounts.Root, instance.Accounts.ServiceId);
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var publicKey = DeviceAuthorization.ExportPublicKey(key);
            var request = Guid.NewGuid().ToString("N");
            instance.Accounts.CreateEnrollment(new(request, publicKey, "电脑 A", "gui"));
            var pin = Convert.ToBase64String(ServiceSecurity.ComputeSpkiPin(instance.Certificate));
            var document = instance.Accounts.ApproveEnrollment(owner, request, space.Id, origin, pin, signing, true, false);
            var connection = new DeviceConnection(origin, instance.Accounts.ServiceId, pin, DeviceAuthorization.ExportPublicKey(signing), "test", request, "电脑 A", "gui");
            var bridge = new AddressBridge(address, DeviceAccountClient.CreateTransport(pin));
            using var client = new DeviceAccountClient(connection, key, value => connection = value, bridge);
            await client.ImportAsync(DeviceAuthorization.WriteFileDocument(document));
            Assert.NotNull(connection.Session);
            using var local = new PicerDatabase(Path.Combine(root, "local", "picer.db"));
            var source = local.AddSource(@"C:\Photos");
            var excluded = local.AddSource(@"D:\Private");
            local.UpsertMirroredImage(new ImageRecord { Id = 2, SourceId = excluded.Id, Path = @"D:\Private\private.jpg", FileSize = 101,
                ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow, Sha256 = new string('d', 64), ContentSha256 = new string('d', 64) });
            local.UpsertMirroredImage(new ImageRecord { Id = 1, SourceId = source.Id, Path = @"C:\Photos\one.jpg", FileSize = 100,
                ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow, Gps = "31.2,121.5", Sha256 = new string('c', 64), ContentSha256 = new string('c', 64) });
            local.UpsertMirroredPerson(new PersonSummary(1, "家人", 0, 0));
            local.UpsertMirroredFaces([new(1, 1, "model-v1", 0, 0, 0, 10, 10, 0.99, null, null, DateTime.UtcNow.ToString("O"))],
                [new(1, 1, "confirmed", 0.99, "user", DateTime.UtcNow.ToString("O"))]);
            local.UpsertMirroredTag(new Tag { Id = 1, Name = "旅行", CreatedUtc = DateTime.UtcNow });
            local.UpsertMirroredImageTag(1, 1);
            local.UpsertMirroredVideoMetadata(new([], [], [], [], [], [], [], [], CloudVisionAnalyses:
                [new(1, "test", "test-model", "test-prompt", "completed", "海边旅行的照片", "{}", DateTime.UtcNow.ToString("O"), 100, DateTime.UtcNow.ToString("O"))]));
            var publisher = new SpaceSyncPublisher(client, local, Path.Combine(root, "snapshots"));
            var scope = new SpaceUploadScope([source.Id], People: !restrictScope, PreciseLocation: !restrictScope);
            bridge.FailNextCommittedBatch = true;
            await Assert.ThrowsAsync<HttpRequestException>(() => publisher.PublishAsync(scope: scope));
            using (var pending = local.Command("SELECT COUNT(*) FROM sync_outbox")) Assert.Equal(1L, pending.ExecuteScalar());
            Assert.True(await publisher.PublishAsync(scope: scope) >= 3);
            using (var pending = local.Command("SELECT COUNT(*) FROM sync_outbox")) Assert.Equal(0L, pending.ExecuteScalar());
            using (var exported = local.Command("SELECT COUNT(*) FROM sync_exports")) Assert.Equal(0L, exported.ExecuteScalar());
            using var remote = new PicerDatabase(Path.Combine(instance.Accounts.SpaceRoot(space.Id), "picer.db"));
            var remoteImage = Assert.Single(remote.GetImages(includeMissing: true));
            Assert.Equal(restrictScope ? null : "31.2,121.5", remoteImage.Gps);
            Assert.Equal(restrictScope ? 0 : 1, new PeopleService(remote).GetAll().Count);
            Assert.Equal(restrictScope ? 0 : 1, remote.GetAllMirroredFaces().Count);
            Assert.Equal("旅行", Assert.Single(SemanticEngine.GetImageTags(remote, remoteImage.Id)).Name);
            Assert.Equal("海边旅行的照片", Assert.Single(remote.GetAllMirroredCloudVisionAnalyses()).Summary);
            Assert.Equal(0, await publisher.PublishAsync(scope: scope));
            Assert.Single(remote.GetImages(includeMissing: true));
            Assert.Single(remote.GetAllMirroredCloudVisionAnalyses());
            Assert.Equal(2, local.GetImages(includeMissing: true).Count());
            Assert.Equal(2, local.GetSources().Count());
            Assert.Equal("31.2,121.5", local.GetImage(1)!.Gps);
            Assert.Single(new PeopleService(local).GetAll());
            Assert.Single(local.GetAllMirroredFaces());
            using (var change = local.Command("UPDATE images SET file_size=file_size+1 WHERE id=1")) change.ExecuteNonQuery();
            Assert.Equal(1, await publisher.PublishAsync(scope: scope));
            Assert.Equal(101, Assert.Single(remote.GetImages(includeMissing: true)).FileSize);
            Assert.Single(remote.GetAllMirroredCloudVisionAnalyses());
            Assert.Equal(0, await publisher.PublishAsync(scope: scope));
            // An owner purge invalidates old publications. The next upload reconciles
            // against remaining identities instead of resurrecting its old local copy.
            using (var markMissing = remote.Command("UPDATE images SET status='missing' WHERE id=$id"))
            { markMissing.Parameters.AddWithValue("$id", remoteImage.Id); markMissing.ExecuteNonQuery(); }
            using var gateway = new Picer.Core.Gateway.HttpIndexGateway(client.SpaceClient);
            await gateway.GetChangesAsync(new());
            Assert.True(await gateway.PurgeMissingImageAsync(remoteImage.Id));
            Assert.Empty(remote.GetImages(includeMissing: true));
            await publisher.PublishAsync(scope: scope);
            Assert.Null(local.GetImage(1));
            Assert.NotNull(local.GetImage(2)); // The unselected local source is untouched.
            Assert.Empty(remote.GetImages(includeMissing: true));
            Assert.Equal(0, await publisher.PublishAsync(scope: scope));
        }
        finally
        {
            await instance.App.StopAsync(); await instance.App.DisposeAsync(); instance.Certificate.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class AddressBridge(string address, HttpMessageHandler transport) : DelegatingHandler(transport)
    {
        public bool FailNextCommittedBatch { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Host = "picer.test";
            request.RequestUri = new Uri(address + request.RequestUri!.PathAndQuery);
            var response = await base.SendAsync(request, cancellationToken);
            if (FailNextCommittedBatch && request.RequestUri.AbsolutePath.EndsWith("/api/sync/v2/batches", StringComparison.Ordinal) && response.IsSuccessStatusCode)
            {
                FailNextCommittedBatch = false;
                response.Dispose();
                throw new HttpRequestException("simulated_disconnect_after_commit");
            }
            return response;
        }
    }
}
