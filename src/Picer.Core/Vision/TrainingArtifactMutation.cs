using System.Text.Json;
using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.People;

namespace Picer.Core.Vision;

public sealed record TrainingArtifactMutation(TrainingArtifactData Artifact, string ExpectedEpoch, string ExpectedActiveArtifact);
public sealed record TrainingApplicationBaseline(string Epoch, string ActiveArtifact);
public sealed record LearnedTrainingApplication(string ArtifactId, string DatasetId, string Endpoint, string ModelVersion, string Json);
public sealed record LearnedTrainingMutation(LearnedTrainingApplication Application, string ExpectedEpoch, string ExpectedActiveArtifact);

public sealed class TrainingArtifactMutationService(PicerDatabase database)
{
    public void ApplyLearned(LearnedTrainingMutation request, SqliteTransaction? externalTransaction = null)
    {
        var application = request?.Application;
        if (application is null || string.IsNullOrWhiteSpace(application.ArtifactId) || application.ArtifactId.Length > 1024 ||
            string.IsNullOrWhiteSpace(application.ModelVersion) || application.ModelVersion.Length > 256 ||
            application.Json is null || application.Json.Length > 16 * 1024 * 1024)
            throw new ArgumentException("invalid_training_artifact");
        using var ownedTransaction = externalTransaction is null ? database.BeginTransaction() : null;
        var transaction = externalTransaction ?? ownedTransaction!;
        using var epoch = database.Command("SELECT epoch FROM mirror_revision WHERE id=1"); epoch.Transaction = transaction;
        if (request!.ExpectedEpoch != epoch.ExecuteScalar() as string) throw new InvalidOperationException("space_epoch_reset_required");
        var repository = new VisionAnalysisRepository(database);
        var current = repository.GetActiveFaceAdapterVersion(application.ModelVersion);
        if (current != request.ExpectedActiveArtifact && current != application.ArtifactId)
            throw new InvalidOperationException("training_artifact_changed");
        string? publisher = null;
        var storedDocument = false;
        using (var stored = database.Command("SELECT dataset_id,endpoint,artifact_json FROM face_training_artifacts WHERE artifact_id=$id AND model_version=$model"))
        {
            stored.Transaction = transaction; stored.Parameters.AddWithValue("$id", application.ArtifactId); stored.Parameters.AddWithValue("$model", application.ModelVersion);
            using var row = stored.ExecuteReader();
            if (row.Read())
            {
                storedDocument = true;
                application = application with { DatasetId = row.GetString(0), Endpoint = row.GetString(1), Json = row.GetString(2) };
                if (application.Endpoint.StartsWith("source:", StringComparison.Ordinal)) publisher = application.Endpoint[7..];
            }
        }
        if (!storedDocument) application = application with { Endpoint = "modelserver" };
        var document = JsonSerializer.Deserialize<LearnedDocument>(application.Json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new ArgumentException("invalid_training_artifact");
        if ((!storedDocument && document.Id != application.ArtifactId) ||
            (document.BaseModelVersion ?? document.ModelVersion) != application.ModelVersion ||
            (document.BaseModelVersion is not null && document.ModelVersion is not null && document.BaseModelVersion != document.ModelVersion) ||
            document.People is not { Count: > 0 and <= 10000 } || document.Metrics is null ||
            !double.IsFinite(document.Metrics.FalseAcceptRate) || document.Metrics.FalseAcceptRate is < 0 or > 1 ||
            !double.IsFinite(document.Metrics.FalseRejectRate) || document.Metrics.FalseRejectRate is < 0 or > 1 ||
            document.People.Any(person => person is null || person.PersonId <= 0 || person.Prototype is not { Length: > 0 and <= 4096 } ||
                person.Prototype.Any(value => !float.IsFinite(value)) || !double.IsFinite(person.Threshold) || person.Threshold is < 0 or > 1) ||
            document.People.Select(person => person.PersonId).Distinct().Count() != document.People.Count)
            throw new ArgumentException("invalid_training_artifact");
        var identities = new Dictionary<long, long>();
        if (publisher is not null)
        {
            using var map = database.Command("SELECT CAST(local_key AS INTEGER),target_id FROM sync_active_entity_identities WHERE publisher_id=$publisher AND kind='person'");
            map.Transaction = transaction; map.Parameters.AddWithValue("$publisher", publisher);
            using (var rows = map.ExecuteReader()) while (rows.Read()) identities.Add(rows.GetInt64(0), rows.GetInt64(1));
            if (document.People.Any(person => !identities.ContainsKey(person.PersonId)))
                throw new InvalidOperationException("training_artifact_person_mapping_missing");
        }
        var policies = document.People.Select(person => new FaceLearnedPolicy(publisher is null ? person.PersonId : identities[person.PersonId],
            application.ArtifactId, application.ModelVersion, person.Prototype, person.Threshold)).ToArray();
        repository.ApplyLearnedFaceArtifact(application.ArtifactId, application.DatasetId, application.Endpoint, application.ModelVersion,
            application.Json, document.Metrics.FalseAcceptRate, document.Metrics.FalseRejectRate, policies, transaction,
            publisher is null ? null : identities.Values.ToArray());
        ownedTransaction?.Commit();
    }

    private sealed record LearnedPerson(long PersonId, float[] Prototype, double Threshold);
    private sealed record LearnedMetrics(double FalseAcceptRate, double FalseRejectRate);
    private sealed record LearnedDocument(string Id, string? BaseModelVersion, string? ModelVersion, IReadOnlyList<LearnedPerson> People, LearnedMetrics Metrics);

    public void Apply(TrainingArtifactMutation request, SqliteTransaction? externalTransaction = null)
    {
        if (request?.Artifact is null || string.IsNullOrWhiteSpace(request.Artifact.ArtifactId) ||
            request.Artifact.ArtifactId.Length > 1024 || string.IsNullOrWhiteSpace(request.Artifact.ModelVersion) ||
            request.Artifact.ModelVersion.Length > 256) throw new ArgumentException("invalid_training_artifact");
        using var ownedTransaction = externalTransaction is null ? database.BeginTransaction() : null;
        var transaction = externalTransaction ?? ownedTransaction!;
        using var epoch = database.Command("SELECT epoch FROM mirror_revision WHERE id=1"); epoch.Transaction = transaction;
        if (request.ExpectedEpoch != epoch.ExecuteScalar() as string) throw new InvalidOperationException("space_epoch_reset_required");
        var repository = new VisionAnalysisRepository(database);
        var current = repository.GetActiveFaceAdapterVersion(request.Artifact.ModelVersion);
        if (current != request.ExpectedActiveArtifact && current != request.Artifact.ArtifactId)
            throw new InvalidOperationException("training_artifact_changed");
        var artifact = request.Artifact;
        string? storedJson = null, sourcePublisher = null;
        using (var stored = database.Command("SELECT artifact_json,endpoint FROM face_training_artifacts WHERE artifact_id=$id AND model_version=$model"))
        {
            stored.Transaction = transaction; stored.Parameters.AddWithValue("$id", artifact.ArtifactId); stored.Parameters.AddWithValue("$model", artifact.ModelVersion);
            using var row = stored.ExecuteReader();
            if (row.Read())
            {
                storedJson = row.GetString(0);
                var endpoint = row.GetString(1);
                if (endpoint.StartsWith("source:", StringComparison.Ordinal)) sourcePublisher = endpoint[7..];
            }
        }
        if (storedJson is not null)
        {
            artifact = JsonSerializer.Deserialize<TrainingArtifactData>(storedJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new ArgumentException("invalid_training_artifact");
            if (artifact.ModelVersion != request.Artifact.ModelVersion || artifact.Prototypes is null)
                throw new ArgumentException("invalid_training_artifact");
            artifact = artifact with { ArtifactId = request.Artifact.ArtifactId };
        }
        IReadOnlyCollection<long>? replacePeople = null;
        if (sourcePublisher is not null)
        {
            var identities = new Dictionary<long, long>();
            using var map = database.Command("SELECT CAST(local_key AS INTEGER),target_id FROM sync_active_entity_identities WHERE publisher_id=$publisher AND kind='person'");
            map.Transaction = transaction; map.Parameters.AddWithValue("$publisher", sourcePublisher);
            using (var rows = map.ExecuteReader()) while (rows.Read()) identities.Add(rows.GetInt64(0), rows.GetInt64(1));
            if (artifact.Prototypes.Any(value => !identities.ContainsKey(value.PersonId)))
                throw new InvalidOperationException("training_artifact_person_mapping_missing");
            artifact = artifact with { Prototypes = artifact.Prototypes.Select(value => value with { PersonId = identities[value.PersonId] }).ToArray() };
            replacePeople = identities.Values.ToArray();
        }
        // The signed original stays byte-for-byte in storage. Only the runtime
        // person references are translated into this space's identity domain.
        repository.ApplyAgentTrainingArtifact(artifact, transaction, storedJson is not null, replacePeople);
        ownedTransaction?.Commit();
    }
}
