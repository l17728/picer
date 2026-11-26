using System.Net.Http.Json;
using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Gateway;

namespace Picer.Core.Identity;

public sealed partial class SpaceSyncPublisher
{
    /// <summary>
    /// Verifies that a mode-switch publish reached the account service and
    /// represents the complete local index, with no unfinished local journal.
    /// This is deliberately stricter than an ordinary source-scoped publish.
    /// </summary>
    public async Task<string> VerifyCompleteIndexPublishedAsync(SpaceUploadScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = accountClient.Connection.Session ?? throw new InvalidOperationException("device_login_required");
            using var database = localDatabase.OpenIndependentConnection();
            var localSources = database.GetSources().Select(source => source.Id).Order().ToArray();
            var selectedSources = scope.SourceIds.Distinct().Order().ToArray();
            if (localSources.Length != selectedSources.Length || !localSources.SequenceEqual(selectedSources))
                throw new InvalidOperationException("index_mode_switch_source_scope_incomplete");
            if (!scope.Photos && database.GetImageCount(includeMissing: true) > 0 ||
                !scope.Videos && new Picer.Core.Video.VideoRepository(database).GetAll().Count > 0)
                throw new InvalidOperationException("index_mode_switch_media_scope_incomplete");
            var localMetadata = IndexSyncClient.BuildMetadataSnapshot(database);
            if (!scope.People && (HasRows(database, "people") || localMetadata.Faces.Count > 0 ||
                    localMetadata.FaceAssignments.Count > 0 || localMetadata.VideoFacesOrEmpty.Count > 0 ||
                    localMetadata.FaceReviewStatesOrEmpty.Count > 0 || localMetadata.FaceCandidateClustersOrEmpty.Count > 0 ||
                    localMetadata.FaceCandidateMembersOrEmpty.Count > 0 || localMetadata.FaceTrainingArtifactsOrEmpty.Count > 0 ||
                    localMetadata.FacePersonPrototypesOrEmpty.Count > 0 || localMetadata.FacePersonPoliciesOrEmpty.Count > 0) ||
                !scope.PreciseLocation && (localMetadata.Locations.Any(location => location.HasGps ||
                    location.Country is not null || location.Province is not null || location.City is not null ||
                    location.County is not null || location.District is not null || location.Town is not null ||
                    location.Village is not null) || HasRows(database, "images", "gps") || HasRows(database, "videos", "gps")))
                throw new InvalidOperationException("index_mode_switch_metadata_scope_incomplete");

            var scopeHash = DeviceAuthorization.Digest(JsonSerializer.Serialize(
                scope with { SourceIds = selectedSources }, DeviceAuthorization.Json));
            string library;
            using (var query = database.Command("SELECT library_id FROM library_identity WHERE id=1"))
                library = query.ExecuteScalar() as string ?? throw new InvalidDataException("publisher_library_missing");

            string publisherId;
            string epoch;
            long sequence;
            using (var query = database.Command("""
                SELECT p.publisher_id,p.epoch,p.last_sequence,p.uploaded_media_revision,p.uploaded_metadata_revision,
                       p.uploaded_scope_hash,p.metadata_protocol,r.revision,r.metadata_revision
                FROM sync_publishers p CROSS JOIN mirror_revision r
                WHERE p.library_id=$library AND p.incarnation_id=$device AND p.device_grant_id=$grant
                    AND r.id=1 AND p.state='publishing'
                """))
            {
                query.Parameters.AddWithValue("$library", library);
                query.Parameters.AddWithValue("$device", session.DeviceId);
                query.Parameters.AddWithValue("$grant", session.GrantId);
                using var row = query.ExecuteReader();
                if (!row.Read()) throw new InvalidOperationException("index_mode_switch_publisher_missing");
                publisherId = row.GetString(0);
                epoch = row.GetString(1);
                sequence = row.GetInt64(2);
                if (row.GetInt64(3) != row.GetInt64(7) || row.GetInt64(4) != row.GetInt64(8))
                    throw new InvalidOperationException("index_mode_switch_local_changed_during_sync");
                if (row.GetString(5) != scopeHash || row.GetInt32(6) != SpaceSyncProtocol.MetadataVersion)
                    throw new InvalidOperationException("index_mode_switch_scope_checkpoint_mismatch");
            }

            if (HasPendingRows(database, "sync_exports", publisherId) || HasPendingRows(database, "sync_outbox", publisherId) ||
                HasPendingRows(database, "sync_pending_identity_adoptions", publisherId) ||
                localDatabase.GetPendingMetadataEdits().Count > 0 || localDatabase.GetDescriptionOutbox(pendingOnly: true).Count > 0 ||
                localDatabase.GetMetadataConflictSnapshot().Reviews.Count > 0 || localDatabase.GetVideoOwnershipReviews().Count > 0 ||
                localDatabase.CountPendingCatalogSynchronization(publisherId) > 0)
                throw new InvalidOperationException("index_mode_switch_local_journal_pending");

            using var response = await accountClient.SpaceClient.PostAsJsonAsync("api/sync/v2/publishers",
                new SpacePublisherRequest(library, session.DeviceId), DeviceAuthorization.Json, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var remote = await response.Content.ReadFromJsonAsync<SpacePublisher>(DeviceAuthorization.Json, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("index_mode_switch_remote_publisher_missing");
            if (remote.PublisherId != publisherId || remote.Epoch != epoch || remote.LastSequence != sequence ||
                remote.IncarnationId != session.DeviceId || remote.MetadataProtocol != SpaceSyncProtocol.MetadataVersion)
                throw new InvalidDataException("index_mode_switch_remote_checkpoint_mismatch");
            return publisherId;
        }
        finally { _gate.Release(); }
    }

    private static bool HasRows(PicerDatabase database, string table, string? column = null)
    {
        if (table is not ("people" or "image_locations" or "images" or "videos") || column is not (null or "gps"))
            throw new ArgumentException("invalid_scope_probe");
        using var query = database.Command(column is null
            ? $"SELECT EXISTS(SELECT 1 FROM {table})"
            : $"SELECT EXISTS(SELECT 1 FROM {table} WHERE {column} IS NOT NULL)");
        return Convert.ToInt64(query.ExecuteScalar()) != 0;
    }

    private static bool HasPendingRows(PicerDatabase database, string table, string publisherId)
    {
        if (table is not ("sync_exports" or "sync_outbox" or "sync_pending_identity_adoptions"))
            throw new ArgumentException("invalid_pending_probe");
        using var query = database.Command($"SELECT EXISTS(SELECT 1 FROM {table} WHERE publisher_id=$publisher)");
        query.Parameters.AddWithValue("$publisher", publisherId);
        return Convert.ToInt64(query.ExecuteScalar()) != 0;
    }
}
