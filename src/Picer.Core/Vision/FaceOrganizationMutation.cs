using Picer.Core.Data;
using Picer.Core.People;

namespace Picer.Core.Vision;

public sealed record FaceOrganizationRequest(string ModelVersion, string Mode, string ExpectedEpoch);
public sealed record FaceCandidateRemovalRequest(long ClusterId, long FaceId, string ExpectedEpoch);
public sealed record FaceOrganizationResult(FaceSuggestionResult Suggestions, CandidateClusteringResult Clusters);

/// <summary>Runs derived face organization against the authoritative database, preserving manual identities.</summary>
public sealed class FaceOrganizationMutationService(PicerDatabase database)
{
    public void RemoveCandidate(FaceCandidateRemovalRequest request)
    {
        if (request is null || request.ClusterId <= 0 || request.FaceId <= 0) throw new ArgumentException("invalid_face_candidate");
        using var transaction = database.BeginTransaction();
        using var epoch = database.Command("SELECT epoch FROM mirror_revision WHERE id=1");
        epoch.Transaction = transaction;
        if (string.IsNullOrWhiteSpace(request.ExpectedEpoch) || request.ExpectedEpoch != epoch.ExecuteScalar() as string)
            throw new InvalidOperationException("space_epoch_reset_required");
        using var current = database.Command("SELECT cluster_id FROM face_candidate_members WHERE face_id=$face");
        current.Transaction = transaction; current.Parameters.AddWithValue("$face", request.FaceId);
        if (current.ExecuteScalar() is long cluster && cluster != request.ClusterId)
            throw new InvalidOperationException("face_candidate_changed");
        using var remove = database.Command("DELETE FROM face_candidate_members WHERE cluster_id=$cluster AND face_id=$face");
        remove.Transaction = transaction; remove.Parameters.AddWithValue("$cluster", request.ClusterId); remove.Parameters.AddWithValue("$face", request.FaceId);
        remove.ExecuteNonQuery(); transaction.Commit();
    }

    public FaceOrganizationResult Apply(FaceOrganizationRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ModelVersion) || request.ModelVersion.Length > 256 ||
            request.Mode is not ("suggest" or "cluster" or "organize"))
            throw new ArgumentException("invalid_face_organization");
        using var epoch = database.Command("SELECT epoch FROM mirror_revision WHERE id=1");
        if (string.IsNullOrWhiteSpace(request.ExpectedEpoch) || request.ExpectedEpoch != epoch.ExecuteScalar() as string)
            throw new InvalidOperationException("space_epoch_reset_required");
        cancellationToken.ThrowIfCancellationRequested();
        var repository = new VisionAnalysisRepository(database);
        var suggestions = request.Mode == "cluster" ? new FaceSuggestionResult(0, 0, 0, 0.62)
            : SuggestBySource(repository, request.ModelVersion, request.ExpectedEpoch, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var clusters = request.Mode == "suggest" ? new CandidateClusteringResult(0, 0, 0, 0)
            : new UnknownFaceClusterer(repository).Rebuild(suggestions.AppliedThreshold, modelVersion: request.ModelVersion,
                expectedEpoch: request.ExpectedEpoch, cancellationToken: cancellationToken);
        return new(suggestions, clusters);
    }

    private FaceSuggestionResult SuggestBySource(VisionAnalysisRepository repository, string modelVersion, string expectedEpoch, CancellationToken cancellationToken)
    {
        var owners = new List<string>();
        using (var query = database.Command("""
            SELECT DISTINCT o.owner_key FROM sync_image_owners o JOIN detected_faces f ON f.image_id=o.image_id
            WHERE f.model_version=$model AND o.owner_key NOT LIKE 'ambiguous-source:%'
            """))
        {
            query.Parameters.AddWithValue("$model", modelVersion);
            using var rows = query.ExecuteReader(); while (rows.Read()) owners.Add(rows.GetString(0));
        }
        var engine = new FaceSuggestionEngine(repository, new PeopleService(database));
        var total = new FaceSuggestionResult(0, 0, 0, repository.GetFaceMatchThreshold(modelVersion) ?? 0.55);
        foreach (var owner in owners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = engine.Suggest(modelVersion: modelVersion, ownerKey: owner, expectedEpoch: expectedEpoch, cancellationToken: cancellationToken);
            total = total with
            {
                Considered = total.Considered + current.Considered, Suggested = total.Suggested + current.Suggested,
                BelowThreshold = total.BelowThreshold + current.BelowThreshold,
                LowQualityDeferred = total.LowQualityDeferred + current.LowQualityDeferred,
                AmbiguousDeferred = total.AmbiguousDeferred + current.AmbiguousDeferred,
                NegativeConflictDeferred = total.NegativeConflictDeferred + current.NegativeConflictDeferred,
                MultiPrototypePeople = total.MultiPrototypePeople + current.MultiPrototypePeople,
                NegativeSamples = total.NegativeSamples + current.NegativeSamples
            };
        }
        return total;
    }
}
