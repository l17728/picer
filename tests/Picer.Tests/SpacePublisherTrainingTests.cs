using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Picer.Core.Data;
using Picer.Core.Identity;
using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Vision;
using Picer.IndexService;
using Picer.IndexService.Identity;

namespace Picer.Tests;

public sealed partial class SpacePublisherIntegrationTests
{
    [Fact]
    public async Task RemoteTrainingApplicationRoundTripsThroughAuthenticatedApi()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-training-api-" + Guid.NewGuid().ToString("N"));
        const string origin = "https://picer.test";
        var instance = AccountServiceHost.Build(Path.Combine(root, "service"), origin, 0);
        try
        {
            var address = await IndexServiceHost.StartAsync(instance.App);
            var owner = instance.Accounts.Register("training-user", "test-training-password!", "训练同步测试");
            var space = Assert.Single(instance.Accounts.ListSpaces(owner));
            using var signing = IdentitySigningKey.LoadOrCreate(instance.Accounts.Root, instance.Accounts.ServiceId);
            var pin = Convert.ToBase64String(ServiceSecurity.ComputeSpkiPin(instance.Certificate));
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = Guid.NewGuid().ToString("N");
            instance.Accounts.CreateEnrollment(new(request, DeviceAuthorization.ExportPublicKey(key), "训练同步设备", "gui"));
            var document = instance.Accounts.ApproveEnrollment(owner, request, space.Id, origin, pin, signing, true, false);
            var connection = new DeviceConnection(origin, instance.Accounts.ServiceId, pin, DeviceAuthorization.ExportPublicKey(signing), "test", request, "训练同步设备", "gui");
            using var client = new DeviceAccountClient(connection, key, value => connection = value,
                new AddressBridge(address, DeviceAccountClient.CreateTransport(pin)));
            await client.ImportAsync(DeviceAuthorization.WriteFileDocument(document));
            using var local = new PicerDatabase(Path.Combine(root, "local", "picer.db"));
            var source = local.AddSource(@"C:\Photos");
            local.UpsertMirroredImage(new ImageRecord { Id = 7000, SourceId = source.Id, Path = @"C:\Photos\one.jpg",
                FileSize = 100, ModifiedUtc = DateTime.UnixEpoch, IndexedUtc = DateTime.UtcNow, Status = ImageStatus.Active });
            var person = new PeopleService(local).Create("test-person");
            var repository = new VisionAnalysisRepository(local);
            var first = new TrainingArtifactData("first", "test", "model-a", DateTime.UtcNow,
                [new(person.Id, [1f, 0f], 0.7, 4)], new(4, 1, 1, 1, 0, 0, 1, 0));
            var second = first with { ArtifactId = "second", Prototypes = [new(person.Id, [0f, 1f], 0.8, 4)] };
            repository.ApplyAgentTrainingArtifact(second);
            repository.ApplyAgentTrainingArtifact(first);
            // Simulate a source clock decades ahead of the service. Causal
            // acknowledgement must still allow a later remote application.
            using (var skew = local.Command("UPDATE face_training_artifacts SET applied_utc='2099-01-01T00:00:00.0000000Z'; UPDATE face_person_prototypes SET updated_utc='2099-01-01T00:00:00.0000000Z'"))
                skew.ExecuteNonQuery();
            var publisher = new SpaceSyncPublisher(client, local, Path.Combine(root, "snapshots"));
            var scope = new SpaceUploadScope([source.Id], People: true, PreciseLocation: true);
            await publisher.PublishAsync(scope: scope);
            using var remote = new PicerDatabase(Path.Combine(instance.Accounts.SpaceRoot(space.Id), "picer.db"));
            using var publisherQuery = remote.Command("SELECT publisher_id FROM sync_publishers");
            var publisherId = (string)publisherQuery.ExecuteScalar()!;
            using var epochQuery = remote.Command("SELECT epoch FROM mirror_revision WHERE id=1");
            var epoch = (string)epochQuery.ExecuteScalar()!;
            using var apply = await client.SpaceClient.PostAsJsonAsync("api/service/v1/faces/training/apply",
                new TrainingArtifactMutation(second with { ArtifactId = publisherId + ":second" }, epoch, publisherId + ":first"));
            apply.EnsureSuccessStatusCode();
            using (var unrelatedEdit = local.Command("UPDATE images SET file_size=file_size+1 WHERE id=7000")) unrelatedEdit.ExecuteNonQuery();
            await publisher.PublishAsync(scope: scope);
            Assert.Equal(publisherId + ":second", new VisionAnalysisRepository(remote).GetActiveFaceAdapterVersion("model-a"));
            await publisher.PullMetadataChangesAsync(scope: scope);
            Assert.Equal("second", repository.GetActiveFaceAdapterVersion("model-a"));
            using var threshold = local.Command("SELECT threshold FROM face_person_prototypes WHERE person_id=$person");
            threshold.Parameters.AddWithValue("$person", person.Id);
            Assert.Equal(0.8, Convert.ToDouble(threshold.ExecuteScalar()));
            await publisher.PullMetadataChangesAsync(scope: scope);
            await publisher.PublishAsync(scope: scope);
            Assert.Equal(0, await publisher.PublishAsync(scope: scope));
            Assert.Equal(publisherId + ":second", new VisionAnalysisRepository(remote).GetActiveFaceAdapterVersion("model-a"));
        }
        finally
        {
            await instance.App.StopAsync(); await instance.App.DisposeAsync(); instance.Certificate.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RemoteFaceModelSettingRequiresWriteScopeAndRejectsInvalidValues()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-threshold-api-" + Guid.NewGuid().ToString("N"));
        const string origin = "https://picer.test";
        const string route = "api/service/v1/faces/model-settings";
        var instance = AccountServiceHost.Build(Path.Combine(root, "service"), origin, 0);
        try
        {
            var address = await IndexServiceHost.StartAsync(instance.App);
            var owner = instance.Accounts.Register("threshold-user", "test-threshold-password!", "阈值同步测试");
            var space = Assert.Single(instance.Accounts.ListSpaces(owner));
            using var signing = IdentitySigningKey.LoadOrCreate(instance.Accounts.Root, instance.Accounts.ServiceId);
            var pin = Convert.ToBase64String(ServiceSecurity.ComputeSpkiPin(instance.Certificate));
            async Task<DeviceAccountClient> Open(bool editing)
            {
                var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var request = Guid.NewGuid().ToString("N");
                instance.Accounts.CreateEnrollment(new(request, DeviceAuthorization.ExportPublicKey(key), "阈值设备", "gui"));
                var document = instance.Accounts.ApproveEnrollment(owner, request, space.Id, origin, pin, signing, editing, !editing);
                var connection = new DeviceConnection(origin, instance.Accounts.ServiceId, pin, DeviceAuthorization.ExportPublicKey(signing), "test", request, "阈值设备", "gui");
                var client = new DeviceAccountClient(connection, key, value => connection = value,
                    new AddressBridge(address, DeviceAccountClient.CreateTransport(pin)));
                await client.ImportAsync(DeviceAuthorization.WriteFileDocument(document));
                return client;
            }
            using var editDevice = await Open(true);
            using var readOnlyDevice = await Open(false);

            // Out-of-range values and a device without the write scope must leave the space untouched.
            using (var aboveRange = await editDevice.SpaceClient.PostAsJsonAsync(route, new FaceModelSetting("model-a", 1.5, 3, 20)))
                Assert.Equal(HttpStatusCode.BadRequest, aboveRange.StatusCode);
            using (var negativePairs = await editDevice.SpaceClient.PostAsJsonAsync(route, new FaceModelSetting("model-a", 0.4, -1, 20)))
                Assert.Equal(HttpStatusCode.BadRequest, negativePairs.StatusCode);
            using (var denied = await readOnlyDevice.SpaceClient.PostAsJsonAsync(route, new FaceModelSetting("model-a", 0.9, 3, 20)))
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using (var stored = await editDevice.SpaceClient.PostAsJsonAsync(route, new FaceModelSetting("model-a", 0.415, 12, 340)))
                stored.EnsureSuccessStatusCode();
            // Saving again replaces the one row of the model space instead of adding a second one.
            using (var updated = await editDevice.SpaceClient.PostAsJsonAsync(route, new FaceModelSetting("model-a", 0.43, 14, 352)))
                updated.EnsureSuccessStatusCode();

            using var remote = new PicerDatabase(Path.Combine(instance.Accounts.SpaceRoot(space.Id), "picer.db"));
            var repository = new VisionAnalysisRepository(remote);
            Assert.Equal(0.43, repository.GetFaceMatchThreshold("model-a"));
            Assert.Null(repository.GetFaceMatchThreshold("model-b"));
            using var rows = remote.Command("SELECT calibrated_same_pairs, calibrated_different_pairs FROM face_model_settings");
            using (var row = rows.ExecuteReader())
            {
                Assert.True(row.Read());
                Assert.Equal((14, 352), (row.GetInt32(0), row.GetInt32(1)));
                Assert.False(row.Read());
            }
        }
        finally
        {
            await instance.App.StopAsync(); await instance.App.DisposeAsync(); instance.Certificate.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
