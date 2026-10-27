using Picer.Core.Identity;

namespace Picer.IndexService.Identity;

public sealed partial class SpaceSyncReceiver
{
    public SpaceTrainingStatePage ReadTrainingApplications(SpaceTrainingStateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var transaction = database.BeginTransaction();
        ValidateAdoptionPublisher(request.PublisherId, request.Epoch, request.Scope);
        if (!request.Scope.People || !request.Scope.Photos || !request.Scope.Videos ||
            request.AfterArtifact is null || request.AfterArtifact.Length > 2048)
            throw new UnauthorizedAccessException("training_scope_not_authorized");
        // Aggregates may contain evidence from every root of their origin.
        using (var roots = Sql("SELECT local_source_id FROM sync_source_bindings WHERE publisher_id=$publisher",
            ("$publisher", request.PublisherId)))
        {
            using var rows = roots.ExecuteReader();
            var bound = new HashSet<long>();
            while (rows.Read()) bound.Add(rows.GetInt64(0));
            if (bound.Count == 0 || !bound.SetEquals(request.Scope.SourceIds))
                throw new UnauthorizedAccessException("training_scope_not_authorized");
        }
        var prefix = request.PublisherId + ":";
        var applications = new List<SpaceTrainingApplicationState>();
        using (var query = Sql("""
            SELECT artifact_id,model_version,applied_utc,artifact_json,dataset_id
            FROM face_training_artifacts WHERE endpoint=$endpoint AND state='active'
                AND applied_utc IS NOT NULL AND artifact_id>$after
            ORDER BY artifact_id LIMIT 101
            """, ("$endpoint", "source:" + request.PublisherId), ("$after", prefix + request.AfterArtifact)))
        {
            using var rows = query.ExecuteReader();
            while (rows.Read())
            {
                var id = rows.GetString(0);
                if (!id.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("training_origin_invalid");
                applications.Add(new(id[prefix.Length..], rows.GetString(1), rows.GetString(2),
                    DeviceAuthorization.Digest(rows.GetString(3)), rows.GetString(4) != prefix + "agent"));
            }
        }
        var more = applications.Count > 100;
        if (more) applications.RemoveAt(100);
        transaction.Commit();
        return new(request.PublisherId, request.Epoch, applications,
            applications.Count > 0 ? applications[^1].ArtifactId : request.AfterArtifact, more);
    }
}
