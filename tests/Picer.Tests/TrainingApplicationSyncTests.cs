using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Identity;
using Picer.Core.People;
using Picer.Core.Vision;
using Picer.IndexService.Identity;

namespace Picer.Tests;

public sealed class TrainingApplicationSyncTests
{
    [Fact]
    public void ReceiptsRestoreRuntimeWithoutChangingSignedDocumentAndRejectOldOrForeignReplies()
    {
        using var database = new PicerDatabase(":memory:");
        var person = new PeopleService(database).Create("test-person");
        var repository = new VisionAnalysisRepository(database);
        var artifact = new TrainingArtifactData("first", "test", "model-a", DateTime.UtcNow,
            [new(person.Id, [1f, 0f], 0.7, 4)], new(4, 1, 1, 1, 0, 0, 1, 0));
        repository.ApplyAgentTrainingArtifact(artifact);
        var publisher = new SpacePublisher("publisher", "library", "device", "epoch", 1, SpaceSyncProtocol.MetadataVersion);
        using (var seed = database.Command("""
            INSERT INTO sync_publishers(publisher_id,library_id,incarnation_id,device_grant_id,space_id,state,epoch)
                VALUES('publisher','library','device','grant','space','publishing','epoch');
            UPDATE face_training_artifacts SET state='superseded',applied_utc='2099-01-01T00:00:00.0000000Z';
            DELETE FROM face_person_prototypes;
            """)) seed.ExecuteNonQuery();
        using var document = database.Command("SELECT artifact_json FROM face_training_artifacts WHERE artifact_id='first'");
        var json = (string)document.ExecuteScalar()!;
        TrainingSyncBaseline.Save(database, publisher.PublisherId, "artifact", "first", "model-a",
            TrainingSyncBaseline.Artifact("model-a", json, "superseded", "2099-01-01T00:00:00.0000000Z"));
        var receipt = new SpaceTrainingApplicationState("first", "model-a", "2020-01-01T00:00:00.0000000Z", DeviceAuthorization.Digest(json), false);
        Assert.Throws<InvalidDataException>(() => SpaceSyncPublisher.ApplyTrainingApplications(database, publisher,
            [receipt with { DocumentDigest = "changed" }]));
        Assert.Equal("none", repository.GetActiveFaceAdapterVersion("model-a"));
        SpaceSyncPublisher.ApplyTrainingApplications(database, publisher, [receipt]);
        SpaceSyncPublisher.ApplyTrainingApplications(database, publisher, [receipt]);
        Assert.Equal(json, document.ExecuteScalar());
        Assert.Equal("first", repository.GetActiveFaceAdapterVersion("model-a"));
        using var prototype = database.Command("SELECT COUNT(*) FROM face_person_prototypes");
        Assert.Equal(1L, prototype.ExecuteScalar());
        using var stamp = database.Command("SELECT applied_utc FROM face_training_artifacts WHERE artifact_id='first'");
        Assert.Equal(receipt.AppliedUtc, stamp.ExecuteScalar());
        Assert.Throws<InvalidDataException>(() => SpaceSyncPublisher.ApplyTrainingApplications(database, publisher with { Epoch = "old" }, [receipt]));
        repository.ApplyAgentTrainingArtifact(artifact with { ArtifactId = "newer" });
        SpaceSyncPublisher.ApplyTrainingApplications(database, publisher, [receipt]);
        Assert.Equal("newer", repository.GetActiveFaceAdapterVersion("model-a"));
        using (var purge = database.Command("DELETE FROM face_training_artifacts WHERE artifact_id='first'")) purge.ExecuteNonQuery();
        SpaceSyncPublisher.ApplyTrainingApplications(database, publisher, [receipt]);
        Assert.Null(document.ExecuteScalar());
        using var suppression = database.Command("SELECT COUNT(*) FROM sync_suppression");
        Assert.Equal(0L, suppression.ExecuteScalar());
    }

    [Fact]
    public void ServerReturnsOnlyOriginApplicationsWithFullExplicitPeopleScope()
    {
        using var database = new PicerDatabase(":memory:");
        var device = new AuthorizedDevice(Guid.NewGuid().ToString("N"), "account", "space", Guid.NewGuid().ToString("N"),
            "public", "gui", AccountStore.EditorScopes, 1, 1);
        var receiver = new SpaceSyncReceiver(database, device);
        var publisher = receiver.Register(new(Guid.NewGuid().ToString("N"), device.DeviceId));
        var source = database.AddSource(Path.Combine(Path.GetTempPath(), "picer-training-origin"));
        using (var seed = database.Command("""
            INSERT INTO sync_source_bindings(publisher_id,local_source_id,source_id,source_uuid,original_path)
                VALUES($publisher,1,$source,lower(hex(randomblob(16))),'origin');
            INSERT INTO face_training_artifacts(artifact_id,dataset_id,endpoint,model_version,artifact_json,
                false_accept_rate,false_reject_rate,state,created_utc,applied_utc)
                VALUES($id,$dataset,$endpoint,'model','{}',0,0,'active','2020-01-01','2020-01-02'),
                ('foreign:artifact','foreign:agent','source:foreign','other-model','{}',0,0,'active','2020-01-01','2020-01-02');
            """))
        {
            seed.Parameters.AddWithValue("$publisher", publisher.PublisherId); seed.Parameters.AddWithValue("$source", source.Id);
            seed.Parameters.AddWithValue("$id", publisher.PublisherId + ":artifact");
            seed.Parameters.AddWithValue("$dataset", publisher.PublisherId + ":agent");
            seed.Parameters.AddWithValue("$endpoint", "source:" + publisher.PublisherId); seed.ExecuteNonQuery();
        }
        var request = new SpaceTrainingStateRequest(publisher.PublisherId, publisher.Epoch, new([1], People: true));
        var page = receiver.ReadTrainingApplications(request);
        var receipt = Assert.Single(page.Applications);
        Assert.Equal("artifact", receipt.ArtifactId); Assert.False(receipt.Learned);
        Assert.Equal(DeviceAuthorization.Digest("{}"), receipt.DocumentDigest);
        Assert.Empty(receiver.ReadTrainingApplications(request with { AfterArtifact = page.AfterArtifact }).Applications);
        Assert.Throws<UnauthorizedAccessException>(() => receiver.ReadTrainingApplications(request with { Scope = request.Scope with { People = false } }));
        Assert.Throws<UnauthorizedAccessException>(() => receiver.ReadTrainingApplications(request with { Scope = request.Scope with { SourceIds = [2] } }));
        Assert.Throws<InvalidOperationException>(() => receiver.ReadTrainingApplications(request with { Epoch = "old" }));
    }
}
