using System.Net.Http.Json;
using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.People;
using Picer.Core.Vision;

namespace Picer.Core.Identity;

public sealed partial class SpaceSyncPublisher
{
    private async Task PullTrainingApplicationsAsync(PicerDatabase database, SpacePublisher publisher,
        SpaceUploadScope scope, CancellationToken cancellationToken)
    {
        if (!scope.People || !scope.Photos || !scope.Videos ||
            !database.GetSources().Select(source => source.Id).ToHashSet().SetEquals(scope.SourceIds)) return;
        var scopeHash = DeviceAuthorization.Digest(JsonSerializer.Serialize(scope with
            { SourceIds = scope.SourceIds.Distinct().Order().ToArray() }, DeviceAuthorization.Json));
        using (var published = database.Command("SELECT 1 FROM sync_publishers WHERE publisher_id=$publisher AND uploaded_scope_hash=$scope AND NOT EXISTS(SELECT 1 FROM sync_exports WHERE publisher_id=$publisher)"))
        {
            published.Parameters.AddWithValue("$publisher", publisher.PublisherId);
            published.Parameters.AddWithValue("$scope", scopeHash);
            if (published.ExecuteScalar() is null) return;
        }
        using (var available = database.Command("SELECT 1 FROM face_training_artifacts LIMIT 1"))
            if (available.ExecuteScalar() is null) return;
        SpaceSyncProtocol.RequireMetadataSupport(publisher);
        var after = "";
        while (true)
        {
            using var response = await accountClient.SpaceClient.PostAsJsonAsync("api/sync/v2/training-applications",
                new SpaceTrainingStateRequest(publisher.PublisherId, publisher.Epoch, scope, after),
                DeviceAuthorization.Json, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<SpaceTrainingStatePage>(DeviceAuthorization.Json, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException("training_state_missing");
            if (page.PublisherId != publisher.PublisherId || page.Epoch != publisher.Epoch || page.Applications is null ||
                page.Applications.Count > 100 || page.HasMore && (page.Applications.Count == 0 ||
                string.CompareOrdinal(page.AfterArtifact, after) <= 0)) throw new InvalidDataException("training_state_invalid");
            ApplyTrainingApplications(database, publisher, page.Applications);
            if (!page.HasMore) break;
            after = page.AfterArtifact;
        }
    }

    internal static void ApplyTrainingApplications(PicerDatabase database, SpacePublisher publisher,
        IReadOnlyList<SpaceTrainingApplicationState> applications)
    {
        using var transaction = database.BeginTransaction();
        using (var owner = database.Command("SELECT 1 FROM sync_publishers WHERE publisher_id=$publisher AND epoch=$epoch AND state='publishing'"))
        {
            owner.Parameters.AddWithValue("$publisher", publisher.PublisherId); owner.Parameters.AddWithValue("$epoch", publisher.Epoch);
            if (owner.ExecuteScalar() is null) throw new InvalidDataException("training_state_publisher_mismatch");
        }
        Execute(database, transaction, "INSERT OR IGNORE INTO sync_suppression(id) VALUES(1)");
        var repository = new VisionAnalysisRepository(database);
        var service = new TrainingArtifactMutationService(database);
        var dirtyModels = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var application in applications)
        {
            if (!DateTimeOffset.TryParse(application.AppliedUtc, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _)) throw new InvalidDataException("training_state_invalid");
            string json, dataset, endpoint;
            using (var existing = database.Command("SELECT artifact_json,dataset_id,endpoint,applied_utc,state FROM face_training_artifacts WHERE artifact_id=$id AND model_version=$model"))
            {
                existing.Parameters.AddWithValue("$id", application.ArtifactId); existing.Parameters.AddWithValue("$model", application.ModelVersion);
                using var row = existing.ExecuteReader();
                // Explicit local purge must not recreate an artifact from an old receipt.
                if (!row.Read()) continue;
                json = row.GetString(0); dataset = row.GetString(1); endpoint = row.GetString(2);
                if (!row.IsDBNull(3) && row.GetString(3) == application.AppliedUtc && row.GetString(4) == "active") continue;
            }
            // Compare against acknowledged content, never two computers' clocks.
            // Unpublished local applications are retained until their own upload.
            if (!dirtyModels.TryGetValue(application.ModelVersion, out var dirty))
                dirtyModels[application.ModelVersion] = dirty = HasPendingTrainingApplication(database, publisher.PublisherId, application.ModelVersion);
            if (dirty) continue;
            if (DeviceAuthorization.Digest(json) != application.DocumentDigest)
                throw new InvalidDataException("training_artifact_document_changed");
            using var epoch = database.Command("SELECT epoch FROM mirror_revision WHERE id=1");
            var localEpoch = (string)epoch.ExecuteScalar()!;
            var current = repository.GetActiveFaceAdapterVersion(application.ModelVersion);
            if (application.Learned)
                service.ApplyLearned(new(new(application.ArtifactId, dataset, endpoint, application.ModelVersion, json), localEpoch, current), transaction);
            else
            {
                var artifact = JsonSerializer.Deserialize<TrainingArtifactData>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidDataException("training_artifact_invalid");
                service.Apply(new(artifact with { ArtifactId = application.ArtifactId }, localEpoch, current), transaction);
            }
            // Keep the originating application clock, so replay is a no-op and
            // importing a receipt cannot manufacture a newer competing edit.
            Execute(database, transaction, "UPDATE face_training_artifacts SET applied_utc=$time WHERE artifact_id=$id",
                ("$time", application.AppliedUtc), ("$id", application.ArtifactId));
            Execute(database, transaction, application.Learned
                ? "UPDATE face_person_policies SET updated_utc=$time WHERE artifact_id=$id"
                : "UPDATE face_person_prototypes SET updated_utc=$time WHERE model_version=$id",
                ("$time", application.AppliedUtc), ("$id", application.Learned ? application.ArtifactId : application.ModelVersion));
        }
        Execute(database, transaction, "DELETE FROM sync_suppression WHERE id=1");
        transaction.Commit();
    }

    private static bool HasPendingTrainingApplication(PicerDatabase database, string publisher, string model)
    {
        using var query = database.Command("""
            SELECT a.artifact_json,a.state,a.applied_utc,b.fingerprint
            FROM face_training_artifacts a LEFT JOIN sync_training_baselines b
                ON b.publisher_id=$publisher AND b.kind='artifact' AND b.row_key=a.artifact_id
            WHERE a.model_version=$model
            """);
        query.Parameters.AddWithValue("$publisher", publisher); query.Parameters.AddWithValue("$model", model);
        using var rows = query.ExecuteReader();
        while (rows.Read())
            if (rows.IsDBNull(3) || rows.GetString(3) != TrainingSyncBaseline.Artifact(model, rows.GetString(0), rows.GetString(1),
                rows.IsDBNull(2) ? null : rows.GetString(2))) return true;
        return false;
    }
}
