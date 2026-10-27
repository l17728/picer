using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.People;
using Picer.Core.Vision;
using System.Text.Json;

namespace Picer.Tests;

public sealed class TrainingApplicationAtomicityTests
{
    [Fact]
    public void LearnedApplicationIsAtomicAndPreservesOtherModelPolicies()
    {
        using var database = new PicerDatabase(":memory:");
        var people = new PeopleService(database); var first = people.Create("first"); var second = people.Create("second");
        var repository = new VisionAnalysisRepository(database);
        repository.ApplyLearnedFaceArtifact("old", "dataset", "test", "model-b", "{}", 0, 0,
            [new(second.Id, "old", "model-b", [0f, 1f], 0.9)]);
        var json = JsonSerializer.Serialize(new { id = "learned", baseModelVersion = "model-a",
            people = new[] { new { personId = first.Id, prototype = new[] { 1f, 0f }, threshold = 0.7 } },
            metrics = new { falseAcceptRate = 0.0, falseRejectRate = 0.0 } });
        using var epochQuery = database.Command("SELECT epoch FROM mirror_revision WHERE id=1");
        var epoch = (string)epochQuery.ExecuteScalar()!;
        var service = new TrainingArtifactMutationService(database);
        var request = new LearnedTrainingMutation(new("learned", "dataset", "modelserver", "model-a", json), epoch, "none");
        service.ApplyLearned(request);
        Assert.Single(repository.GetLearnedFacePolicies("model-a"));
        Assert.Single(repository.GetLearnedFacePolicies("model-b"));
        Assert.Equal("old", repository.GetActiveFaceAdapterVersion("model-b"));
        Assert.Throws<InvalidOperationException>(() => service.ApplyLearned(request with { ExpectedEpoch = "old" }));
        var invalidJson = JsonSerializer.Serialize(new { id = "invalid", baseModelVersion = "model-a",
            people = new[] { new { personId = long.MaxValue, prototype = new[] { 0f, 1f }, threshold = 0.8 } },
            metrics = new { falseAcceptRate = 0.0, falseRejectRate = 0.0 } });
        Assert.Throws<SqliteException>(() => service.ApplyLearned(new(new("invalid", "dataset", "modelserver", "model-a", invalidJson), epoch, "learned")));
        Assert.Equal("learned", repository.GetActiveFaceAdapterVersion("model-a"));
        Assert.Single(repository.GetLearnedFacePolicies("model-a"));
    }

    [Fact]
    public void RemoteApplicationMapsSourcePeopleWithoutRewritingSignedDocument()
    {
        using var database = new PicerDatabase(":memory:");
        var people = new PeopleService(database);
        var other = people.Create("other"); var owned = people.Create("owned");
        var repository = new VisionAnalysisRepository(database);
        var initial = new TrainingArtifactData("previous", "test", "model-a", DateTime.UtcNow,
            [new(other.Id, [0f, 1f], 0.9, 4)], new(4, 1, 1, 1, 0, 0, 1, 0));
        repository.ApplyAgentTrainingArtifact(initial);
        var original = initial with { ArtifactId = "original", Signature = "preserved-document-signature",
            Prototypes = [new(77, [1f, 0f], 0.7, 4)] };
        var json = JsonSerializer.Serialize(original);
        using (var seed = database.Command("""
            INSERT INTO sync_publishers(publisher_id,library_id,incarnation_id,device_grant_id,space_id,state,epoch)
                VALUES('publisher','library','device','grant','space','receiving','epoch');
            INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id,target_id)
                VALUES('publisher','person','77',lower(hex(randomblob(16))),$person);
            INSERT INTO face_training_artifacts(artifact_id,dataset_id,endpoint,model_version,artifact_json,
                false_accept_rate,false_reject_rate,state,created_utc)
                VALUES('publisher:original','publisher:agent','source:publisher','model-a',$json,0,0,'candidate','2026-10-27');
            """))
        { seed.Parameters.AddWithValue("$person", owned.Id); seed.Parameters.AddWithValue("$json", json); seed.ExecuteNonQuery(); }
        using var epochQuery = database.Command("SELECT epoch FROM mirror_revision WHERE id=1");
        var epoch = (string)epochQuery.ExecuteScalar()!;
        var request = new TrainingArtifactMutation(original with { ArtifactId = "publisher:original" }, epoch, "previous");
        var service = new TrainingArtifactMutationService(database);
        service.Apply(request); service.Apply(request); // Safe retry after a lost successful response.
        using var document = database.Command("SELECT artifact_json FROM face_training_artifacts WHERE artifact_id='publisher:original'");
        Assert.Equal(json, document.ExecuteScalar());
        using var prototypes = database.Command("SELECT COUNT(*) FROM face_person_prototypes");
        Assert.Equal(2L, prototypes.ExecuteScalar());
        using var unrelated = database.Command("SELECT state FROM face_training_artifacts WHERE artifact_id='previous'");
        Assert.Equal("active", unrelated.ExecuteScalar());
        using var ownedPrototype = database.Command("SELECT threshold FROM face_person_prototypes WHERE person_id=$person");
        ownedPrototype.Parameters.AddWithValue("$person", owned.Id); Assert.Equal(0.7, Convert.ToDouble(ownedPrototype.ExecuteScalar()));
        Assert.Throws<InvalidOperationException>(() => service.Apply(request with { ExpectedEpoch = "old" }));
        Assert.Throws<InvalidOperationException>(() => service.Apply(request with { Artifact = original with { ArtifactId = "another" } }));
    }

    [Fact]
    public void FailedReplacementRetainsPrototypesAndActiveHistory()
    {
        using var database = new PicerDatabase(":memory:");
        var person = new PeopleService(database).Create("test-person");
        var repository = new VisionAnalysisRepository(database);
        var original = new TrainingArtifactData("first", "test", "model-a", DateTime.UtcNow,
            [new(person.Id, [1f, 0f], 0.7, 4)], new(4, 1, 1, 1, 0, 0, 1, 0));
        repository.ApplyAgentTrainingArtifact(original);
        Assert.Throws<SqliteException>(() => repository.ApplyAgentTrainingArtifact(original with
        { ArtifactId = "invalid-person", Prototypes = [new(long.MaxValue, [0f, 1f], 0.8, 4)] }));
        using var prototype = database.Command("SELECT threshold FROM face_person_prototypes");
        Assert.Equal(0.7, Convert.ToDouble(prototype.ExecuteScalar()));
        Assert.Equal("first", repository.GetActiveFaceAdapterVersion("model-a"));
        Assert.Null(repository.GetFaceTrainingArtifact("invalid-person", "model-a"));
        Assert.Throws<ArgumentException>(() => repository.ApplyAgentTrainingArtifact(original with
        { ArtifactId = "invalid-vector", Prototypes = [new(person.Id, [float.NaN], 0.8, 4)] }));
        repository.ApplyAgentTrainingArtifact(original with { ArtifactId = "second" });
        Assert.Equal("second", repository.GetActiveFaceAdapterVersion("model-a"));
        Assert.Throws<ArgumentException>(() => repository.ApplyAgentTrainingArtifact(original with
        { ArtifactId = "second", ModelVersion = "model-b" }));
        Assert.Equal("second", repository.GetActiveFaceAdapterVersion("model-a"));
        Assert.Equal("none", repository.GetActiveFaceAdapterVersion("model-b"));
    }
}
