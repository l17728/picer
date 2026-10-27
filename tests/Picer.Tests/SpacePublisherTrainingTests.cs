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
}
