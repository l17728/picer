using System.Net.Http.Json;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Video;

namespace Picer.Core.Identity;

public sealed partial class SpaceSyncPublisher
{
    /// <summary>Pulls publisher-owned classification and media-description changes; other publishers' records never enter this database.</summary>
    public async Task<int> PullMetadataChangesAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default, SpaceUploadScope? scope = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var publisherLease = await AcquirePublisherLeaseAsync(cancellationToken).ConfigureAwait(false);
            using var database = localDatabase.OpenIndependentConnection();
            return await PullMetadataChangesCoreAsync(database, progress, cancellationToken, scope).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<int> PullMetadataChangesCoreAsync(PicerDatabase database, IProgress<string>? progress, CancellationToken cancellationToken, SpaceUploadScope? scope)
    {
        var session = accountClient.Connection.Session ?? throw new InvalidOperationException("device_login_required");
        if (!session.Scope.Split(' ').Contains("index.read", StringComparer.Ordinal))
            throw new UnauthorizedAccessException("此设备尚未获准读取照片空间索引。");
        string library;
        using (var query = database.Command("SELECT library_id FROM library_identity WHERE id=1"))
            library = Convert.ToString(query.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)
                ?? throw new InvalidOperationException("library_identity_missing");
        using var registration = await accountClient.SpaceClient.PostAsJsonAsync("api/sync/v2/publishers",
            new SpacePublisherRequest(library, session.DeviceId), cancellationToken).ConfigureAwait(false);
        registration.EnsureSuccessStatusCode();
        var publisher = await registration.Content.ReadFromJsonAsync<SpacePublisher>(DeviceAuthorization.Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("publisher_response_missing");
        if (publisher.LibraryId != library) throw new InvalidDataException("publisher_library_mismatch");
        EnsureLocalPublisher(database, publisher, session);
        scope ??= new(database.GetSources().Select(source => source.Id).ToArray(), People: true, PreciseLocation: true);
        PrepareDownloadScope(database,publisher.PublisherId,scope);
        await ResumeCatalogCompletionsAsync(database,publisher,scope,cancellationToken).ConfigureAwait(false);
        await AdoptRemoteIdentitiesAsync(database, publisher, scope, cancellationToken).ConfigureAwait(false);

        long cursor;
        using (var read = database.Command("SELECT downloaded_sequence FROM sync_publishers WHERE publisher_id=$id"))
        {
            read.Parameters.AddWithValue("$id", publisher.PublisherId);
            cursor = Convert.ToInt64(read.ExecuteScalar());
        }
        var applied = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var response = await accountClient.SpaceClient.GetAsync(
                $"api/sync/v2/metadata-changes?after={cursor}&pageSize=100", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<SpaceMetadataChangePage>(DeviceAuthorization.Json, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("metadata_change_page_missing");
            if (page.PublisherId != publisher.PublisherId || page.Epoch != publisher.Epoch)
                throw new InvalidDataException("metadata_change_publisher_epoch_mismatch");
            if (page.Through < cursor) throw new InvalidDataException("metadata_change_cursor_regressed");
            if (page.HasMore && page.Through == cursor) throw new InvalidDataException("metadata_change_cursor_stalled");
            applied += ApplyDownloadedMetadataPage(database, publisher.PublisherId, page, scope);
            cursor = page.Through;
            progress?.Report(page.HasMore ? $"正在接收照片空间索引更新：已应用 {applied} 项…" : $"照片空间索引更新已同步：{applied} 项。");
            if (!page.HasMore) break;
        } while (true);
        await PullMetadataEditsAsync(database, publisher.PublisherId, publisher.Epoch, progress, cancellationToken, scope).ConfigureAwait(false);
        await PullCatalogRetirementsAsync(database,publisher,scope,progress,cancellationToken).ConfigureAwait(false);
        await PullTrainingApplicationsAsync(database,publisher,scope,cancellationToken).ConfigureAwait(false);
        return applied;
    }

    private async Task PullMetadataEditsAsync(PicerDatabase database, string publisherId, string epoch, IProgress<string>? progress, CancellationToken cancellationToken, SpaceUploadScope? scope)
    {
        await QuarantineArchivedOperationsAsync(database,publisherId,epoch,scope,cancellationToken).ConfigureAwait(false);
        var synchronized=await PullPagedMetadataEditsAsync(database,publisherId,epoch,scope,progress,cancellationToken).ConfigureAwait(false);
        await PullMetadataOperationStatesAsync(database,publisherId,epoch,scope,cancellationToken).ConfigureAwait(false);
        await PullConflictResolutionsAsync(database,publisherId,epoch,scope,cancellationToken).ConfigureAwait(false);
        progress?.Report($"照片空间人工元数据已同步：{synchronized} 项。");
    }

    internal static int ApplyDownloadedMetadataPage(PicerDatabase database, string publisher, SpaceMetadataChangePage page, SpaceUploadScope? scope = null)
    {
        using var transaction = database.BeginTransaction();
        long before;
        using (var state = database.Command("SELECT downloaded_sequence,epoch FROM sync_publishers WHERE publisher_id=$publisher"))
        {
            state.Transaction = transaction; state.Parameters.AddWithValue("$publisher", publisher);
            using var reader = state.ExecuteReader();
            if (!reader.Read()) throw new InvalidDataException("metadata_change_publisher_missing");
            before = reader.GetInt64(0);
            if (page.PublisherId is not null && page.PublisherId != publisher || page.Epoch is not null && page.Epoch != reader.GetString(1))
                throw new InvalidDataException("metadata_change_publisher_epoch_mismatch");
        }
        if (page.Through < before || page.HasMore && page.Through == before || page.Changes is null ||
            page.Changes.Any(change => change.ChangeId <= 0 || change.ChangeId > page.Through || change.MediaKind is not ("image" or "video") ||
                change.Assertions is null || change.Rejections is null || change.Clusters is null || change.ClusterMembers is null))
            throw new InvalidDataException("metadata_change_page_invalid");
        if (page.Through == before) return 0;
        using (var suppression = database.Command("INSERT OR IGNORE INTO sync_suppression(id) VALUES(1)"))
        { suppression.Transaction = transaction; suppression.ExecuteNonQuery(); }
        var applied = 0;
        foreach (var change in page.Changes)
        {
            if (change.ChangeId <= before) continue;
            using var resolve = database.Command("SELECT local_key FROM sync_entity_identities WHERE publisher_id=$publisher AND kind=$kind AND global_id=$global");
            resolve.Transaction = transaction;
            resolve.Parameters.AddWithValue("$publisher", publisher);
            resolve.Parameters.AddWithValue("$kind", change.MediaKind == "video" ? "video" : "image");
            resolve.Parameters.AddWithValue("$global", change.ImageGlobalId);
            if (resolve.ExecuteScalar() is not string localKey || !long.TryParse(localKey, out var localId)) continue;
            if (!IsMediaInScope(database, transaction, change.MediaKind, localId, scope)) continue;
            var authorizedChange = scope is null ? change : change with
            {
                Faces = scope.People ? change.Faces : null,
                FaceAssignments = scope.People ? change.FaceAssignments : null,
                FaceReviewDecisions = scope.People ? change.FaceReviewDecisions : null,
                FaceCandidateClusters = scope.People ? change.FaceCandidateClusters : null,
                FaceCandidateMembers = scope.People ? change.FaceCandidateMembers : null,
                VideoFaces = scope.People ? change.VideoFaces : null,
                Locations = scope.PreciseLocation ? change.Locations : null
            };
            ApplyClassificationChange(database, transaction, publisher, localId, authorizedChange);
            if(change.MediaKind=="image")ApplySceneChanges(database,transaction,publisher,localId,authorizedChange,scope);
            applied++;
        }
        using (var finish = database.Command("UPDATE sync_publishers SET downloaded_sequence=$cursor WHERE publisher_id=$publisher"))
        {
            finish.Transaction = transaction; finish.Parameters.AddWithValue("$cursor", page.Through);
            finish.Parameters.AddWithValue("$publisher", publisher); finish.ExecuteNonQuery();
        }
        using (var suppression = database.Command("DELETE FROM sync_suppression WHERE id=1"))
        { suppression.Transaction = transaction; suppression.ExecuteNonQuery(); }
        transaction.Commit();
        return applied;
    }

    private static bool IsMediaInScope(PicerDatabase database, Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string kind, long id, SpaceUploadScope? scope)
    {
        if (scope is null) return true;
        if (kind == "image" && !scope.Photos || kind == "video" && !scope.Videos) return false;
        using var query = database.Command(kind == "video" ? "SELECT source_id FROM videos WHERE id=$id" : "SELECT source_id FROM images WHERE id=$id");
        query.Transaction = transaction; query.Parameters.AddWithValue("$id", id);
        return query.ExecuteScalar() is long source && scope.SourceIds.Contains(source);
    }

    private static bool IsEditInScope(PicerDatabase database, Microsoft.Data.Sqlite.SqliteTransaction transaction,
        MetadataEdit edit, SpaceUploadScope? scope) => scope is null || (edit.Kind switch
        {
            "space-setting" => true,
            "scene-description" => IsSceneInScope(database,transaction,edit.Field,scope),
            "person" => scope.People,
            "face-state" or "face-rejection" => scope.People && IsFaceEditInScope(database,transaction,edit.EntityId,scope),
            "video-face-state" or "video-face-rejection" => scope.People && IsVideoFaceEditInScope(database,transaction,edit.EntityId,scope),
            "tag-link" => IsMediaInScope(database, transaction, "image", edit.EntityId, scope),
            "photo-classification-confirmation" or "photo-classification-policy"=>IsPhotoEditInScope(database,transaction,edit.EntityId,scope),
            "album-link" => long.TryParse(edit.Field, out var imageId) && IsMediaInScope(database, transaction, "image", imageId, scope),
            "video-tag-link" or "video-concept" => IsVideoEditInScope(database,transaction,edit.EntityId,scope),
            _ => false
        });

    private static bool IsPhotoEditInScope(PicerDatabase database,Microsoft.Data.Sqlite.SqliteTransaction transaction,long imageId,SpaceUploadScope scope)
    {
        if(!scope.Photos) return false;
        using var query=database.Command("""
            SELECT 1 FROM images origin JOIN sync_image_owners oo ON oo.image_id=origin.id
            JOIN images candidate JOIN sync_image_owners co ON co.image_id=candidate.id
            WHERE origin.id=$id AND co.owner_key=oo.owner_key
              AND (candidate.id=origin.id OR (length(origin.sha256)=64 AND lower(candidate.sha256)=lower(origin.sha256)))
              AND candidate.source_id IN (SELECT value FROM json_each($sources)) LIMIT 1
            """);
        query.Transaction=transaction;query.Parameters.AddWithValue("$id",imageId);
        query.Parameters.AddWithValue("$sources",System.Text.Json.JsonSerializer.Serialize(scope.SourceIds));
        return query.ExecuteScalar() is not null;
    }

    private static bool IsFaceEditInScope(PicerDatabase database,Microsoft.Data.Sqlite.SqliteTransaction transaction,long faceId,SpaceUploadScope scope)
    {
        using var query=database.Command("SELECT image_id FROM detected_faces WHERE id=$id");query.Transaction=transaction;query.Parameters.AddWithValue("$id",faceId);
        return query.ExecuteScalar() is long image && IsMediaInScope(database,transaction,"image",image,scope);
    }

    private static bool IsVideoFaceEditInScope(PicerDatabase database,Microsoft.Data.Sqlite.SqliteTransaction transaction,long faceId,SpaceUploadScope scope)
    {
        if(!scope.Videos) return false;
        using var query=database.Command("""
            SELECT 1 FROM video_effective_faces f JOIN videos v ON v.id=f.reference_id
            WHERE f.id=$id AND v.source_id IN (SELECT value FROM json_each($sources)) LIMIT 1
            """);
        query.Transaction=transaction;query.Parameters.AddWithValue("$id",faceId);
        query.Parameters.AddWithValue("$sources",System.Text.Json.JsonSerializer.Serialize(scope.SourceIds));
        return query.ExecuteScalar() is not null;
    }

    private static bool IsVideoEditInScope(PicerDatabase database,Microsoft.Data.Sqlite.SqliteTransaction transaction,long referenceId,SpaceUploadScope scope)
    {
        if (!scope.Videos) return false;
        using var query=database.Command("""
            SELECT EXISTS(SELECT 1 FROM sync_video_owners origin JOIN sync_video_owners candidate
                ON candidate.content_id=origin.content_id AND candidate.owner_key=origin.owner_key
                JOIN videos v ON v.id=candidate.video_id
                WHERE origin.video_id=$id AND v.source_id IN (SELECT value FROM json_each($sources)))
            """);
        query.Transaction=transaction; query.Parameters.AddWithValue("$id",referenceId);
        query.Parameters.AddWithValue("$sources",System.Text.Json.JsonSerializer.Serialize(scope.SourceIds));
        return Convert.ToInt64(query.ExecuteScalar())==1;
    }

    private static void ApplyClassificationChange(PicerDatabase database, Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string publisher, long imageId, SpaceMetadataChange change)
    {
        if (change.MediaKind == "video")
        {
            var contentId = new VideoContentRepository(database).GetContentId(imageId);
            if (change.VideoKeyframes is { } keyframes)
            {
                var retainedKeyframes = keyframes.Select(item => item.Id).ToHashSet();
                foreach (var keyframe in ReadOwnedIds(database, transaction, publisher, "keyframe", "video_keyframes", "video_id", contentId))
                    if (!retainedKeyframes.Contains(keyframe))
                        Execute(database, transaction,
                            change.VideoFaces is null
                                ? "DELETE FROM video_keyframes WHERE id=$id AND NOT EXISTS(SELECT 1 FROM video_detected_faces WHERE keyframe_id=$id)"
                                : "DELETE FROM video_keyframes WHERE id=$id", ("$id", keyframe));
            }
            if (change.VideoFaces is { } videoFaces)
            {
                var retainedFaces = videoFaces.Select(item => item.Id).ToHashSet();
                foreach (var face in ReadOwnedIds(database, transaction, publisher, "video-face", "video_detected_faces", "video_id", contentId))
                    if (!retainedFaces.Contains(face))
                        Execute(database, transaction, "DELETE FROM video_detected_faces WHERE id=$id", ("$id", face));
            }
            if (change.VideoSummaries is { Count: 0 })
                Execute(database, transaction, "DELETE FROM video_summaries WHERE video_id=$content", ("$content", contentId));
            if (change.VideoTranscripts is { Count: 0 })
                Execute(database, transaction, "DELETE FROM video_transcripts WHERE video_id=$content", ("$content", contentId));
            database.UpsertMirroredVideoMetadata(new GatewayMetadata([], [], []) with
            {
                VideoSummaries = change.VideoSummaries?.Select(item => item with { VideoId = imageId }).ToArray() ?? [],
                VideoTranscripts = change.VideoTranscripts?.Select(item => item with { VideoId = imageId }).ToArray() ?? [],
                VideoKeyframes = change.VideoKeyframes?.Select(item => item with { VideoId = imageId }).ToArray() ?? [],
                VideoFaces = change.VideoFaces?.Select(item => item with { VideoId = imageId, Embedding = null }).ToArray() ?? []
            }, externalTransaction: transaction);
            return;
        }
        using (var exists = database.Command("SELECT 1 FROM images WHERE id=$id"))
        {
            exists.Transaction = transaction; exists.Parameters.AddWithValue("$id", imageId);
            if (exists.ExecuteScalar() is null) return;
        }
        // A change includes the current publisher-owned classification snapshot. Prune
        // removed keys and merge retained keys, preserving newer values and user review.
        ReconcileClassification(database, transaction, imageId, change);
        using (var clear = database.Command("""
            DELETE FROM semantic_content_cluster_members WHERE image_id=$image;
            """))
        {
            clear.Transaction = transaction;
            clear.Parameters.AddWithValue("$image", imageId);
            clear.ExecuteNonQuery();
        }
        if (change.Locations is not null)
            Execute(database, transaction, "DELETE FROM image_locations WHERE image_id=$id", ("$id", imageId));
        if (change.PhotoAnalyses is { } photoAnalyses)
        {
            var analysisProviders = photoAnalyses.Select(item => item.Provider).ToHashSet(StringComparer.Ordinal);
            using var analyses = database.Command("SELECT provider FROM cloud_vision_analyses WHERE image_id=$image AND status='completed'");
            analyses.Transaction = transaction; analyses.Parameters.AddWithValue("$image", imageId);
            using var reader = analyses.ExecuteReader();
            var removedProviders = new List<string>();
            while (reader.Read()) if (!analysisProviders.Contains(reader.GetString(0))) removedProviders.Add(reader.GetString(0));
            reader.Close();
            using var remove = database.Command("DELETE FROM cloud_vision_analyses WHERE image_id=$image AND provider=$provider AND status='completed'");
            remove.Transaction = transaction;
            foreach (var provider in removedProviders)
            {
                remove.Parameters.Clear();
                remove.Parameters.AddWithValue("$image", imageId);
                remove.Parameters.AddWithValue("$provider", provider);
                remove.ExecuteNonQuery();
            }
        }
        if (change.Faces is { } photoFaces)
        {
            var retainedFaceIds = photoFaces.Select(face => face.Id).ToHashSet();
            foreach (var faceId in ReadOwnedIds(database, transaction, publisher, "face", "detected_faces", "image_id", imageId))
                if (!retainedFaceIds.Contains(faceId))
                    Execute(database, transaction, "DELETE FROM detected_faces WHERE id=$id", ("$id", faceId));
        }
        if (change.FaceAssignments is { } assignmentSnapshot)
        {
            var retainedAssignments = assignmentSnapshot.Select(item => item.FaceId).ToHashSet();
            foreach (var faceId in ReadOwnedIds(database, transaction, publisher, "face", "detected_faces", "image_id", imageId))
                if (!retainedAssignments.Contains(faceId))
                Execute(database, transaction, "DELETE FROM face_person_assignments WHERE face_id=$id", ("$id", faceId));
        }
        if (change.PhotoAnalyses is { Count: > 0 })
            database.UpsertMirroredVideoMetadata(new GatewayMetadata([], [], []) with
            {
                CloudVisionAnalyses = change.PhotoAnalyses.Select(item => item with { ImageId = imageId }).ToArray()
            }, externalTransaction: transaction);
        foreach (var face in change.Faces ?? []) database.UpsertMirroredFace(face with { Embedding = null, ImageId = imageId }, transaction);
        ApplyFaceCandidateChange(database,transaction,publisher,imageId,change);
        foreach (var assignment in change.FaceAssignments ?? []) database.UpsertMirroredFaceAssignment(assignment, transaction);
        if (change.FaceReviewDecisions is { Count: > 0 }) database.MergeFaceReviewDecisionsInTransaction(change.FaceReviewDecisions, transaction);
        foreach (var location in change.Locations ?? []) database.UpsertMirroredLocation(location with { ImageId = imageId }, transaction);
        foreach (var assertion in change.Assertions)
        {
            var concept = assertion.ConceptId;
            using var command = database.Command("""
                INSERT INTO image_classification_assertions(image_id,content_sha256,concept_id,source_kind,source_version,assertion_state,review_state,
                    confidence,evidence_fingerprint,created_utc,updated_utc,reviewed_utc)
                VALUES($image,$sha,$concept,$source,$version,$assertion,$review,$confidence,$fingerprint,$created,$updated,$reviewed)
                ON CONFLICT(image_id,concept_id,source_kind,source_version) DO UPDATE SET
                    content_sha256=excluded.content_sha256,
                    assertion_state=CASE WHEN excluded.updated_utc>=image_classification_assertions.updated_utc THEN excluded.assertion_state ELSE image_classification_assertions.assertion_state END,
                    review_state=CASE WHEN image_classification_assertions.review_state IN ('accepted','rejected') AND excluded.review_state='pending' THEN image_classification_assertions.review_state
                        WHEN excluded.updated_utc>=image_classification_assertions.updated_utc THEN excluded.review_state ELSE image_classification_assertions.review_state END,
                    confidence=CASE WHEN excluded.updated_utc>=image_classification_assertions.updated_utc THEN excluded.confidence ELSE image_classification_assertions.confidence END,
                    evidence_fingerprint=CASE WHEN excluded.updated_utc>=image_classification_assertions.updated_utc THEN excluded.evidence_fingerprint ELSE image_classification_assertions.evidence_fingerprint END,
                    updated_utc=MAX(image_classification_assertions.updated_utc,excluded.updated_utc),
                    reviewed_utc=CASE WHEN excluded.updated_utc>=image_classification_assertions.updated_utc THEN excluded.reviewed_utc ELSE image_classification_assertions.reviewed_utc END
                """);
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$image", imageId); command.Parameters.AddWithValue("$sha", (object?)assertion.ContentSha256 ?? DBNull.Value);
            command.Parameters.AddWithValue("$concept", concept); command.Parameters.AddWithValue("$source", assertion.SourceKind);
            command.Parameters.AddWithValue("$version", assertion.SourceVersion); command.Parameters.AddWithValue("$assertion", assertion.AssertionState);
            command.Parameters.AddWithValue("$review", assertion.ReviewState); command.Parameters.AddWithValue("$confidence", (object?)assertion.Confidence ?? DBNull.Value);
            command.Parameters.AddWithValue("$fingerprint", (object?)assertion.EvidenceFingerprint ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", assertion.CreatedUtc); command.Parameters.AddWithValue("$updated", assertion.UpdatedUtc);
            command.Parameters.AddWithValue("$reviewed", (object?)assertion.ReviewedUtc ?? DBNull.Value); command.ExecuteNonQuery();
        }
        foreach (var rejection in change.Rejections)
        {
            using var command = database.Command("""
                INSERT INTO image_classification_rejections(image_id,content_sha256,concept_id,source_scope,rejected_utc)
                VALUES($image,NULL,$concept,$scope,$rejected)
                ON CONFLICT(image_id,concept_id,source_scope) WHERE image_id IS NOT NULL
                DO UPDATE SET rejected_utc=MAX(image_classification_rejections.rejected_utc,excluded.rejected_utc)
                """);
            command.Transaction = transaction; command.Parameters.AddWithValue("$image", imageId);
            command.Parameters.AddWithValue("$concept", rejection.ConceptId); command.Parameters.AddWithValue("$scope", rejection.SourceScope);
            command.Parameters.AddWithValue("$rejected", rejection.RejectedUtc); command.ExecuteNonQuery();
        }
        foreach (var cluster in change.Clusters)
        {
            using var command = database.Command("""
                INSERT INTO semantic_content_clusters(cluster_key,model_version,display_name,confidence,member_count,created_utc,updated_utc)
                VALUES($key,$model,$name,$confidence,$count,$created,$updated)
                ON CONFLICT(cluster_key) DO UPDATE SET model_version=excluded.model_version,display_name=excluded.display_name,
                    confidence=excluded.confidence,member_count=excluded.member_count,
                    updated_utc=MAX(semantic_content_clusters.updated_utc,excluded.updated_utc)
                WHERE excluded.updated_utc>=semantic_content_clusters.updated_utc
                """);
            command.Transaction = transaction; command.Parameters.AddWithValue("$key", cluster.ClusterKey);
            command.Parameters.AddWithValue("$model", cluster.ModelVersion); command.Parameters.AddWithValue("$name", cluster.DisplayName);
            command.Parameters.AddWithValue("$confidence", cluster.Confidence); command.Parameters.AddWithValue("$count", cluster.MemberCount);
            command.Parameters.AddWithValue("$created", cluster.CreatedUtc); command.Parameters.AddWithValue("$updated", cluster.UpdatedUtc);
            command.ExecuteNonQuery();
        }
        foreach (var member in change.ClusterMembers)
        {
            using var command = database.Command("INSERT INTO semantic_content_cluster_members(cluster_key,image_id,similarity) VALUES($key,$image,$similarity) ON CONFLICT(cluster_key,image_id) DO UPDATE SET similarity=excluded.similarity");
            command.Transaction = transaction; command.Parameters.AddWithValue("$key", member.ClusterKey);
            command.Parameters.AddWithValue("$image", imageId); command.Parameters.AddWithValue("$similarity", member.Similarity); command.ExecuteNonQuery();
        }
        database.ReplayPhotoClassificationFields(imageId,transaction);
    }

    private static void ReconcileClassification(PicerDatabase database, Microsoft.Data.Sqlite.SqliteTransaction transaction,
        long imageId, SpaceMetadataChange change)
    {
        var retained = change.Assertions.Select(item => (item.ConceptId, item.SourceKind, item.SourceVersion)).ToHashSet();
        var removed = new List<(string Concept, string Source, string Version)>();
        using (var query = database.Command("SELECT concept_id,source_kind,source_version FROM image_classification_assertions WHERE image_id=$image"))
        {
            query.Transaction = transaction; query.Parameters.AddWithValue("$image", imageId);
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                var key = (reader.GetString(0), reader.GetString(1), reader.GetString(2));
                if (!retained.Contains(key)) removed.Add(key);
            }
        }
        foreach (var (concept, source, version) in removed)
            Execute(database, transaction, "DELETE FROM image_classification_assertions WHERE image_id=$image AND concept_id=$concept AND source_kind=$source AND source_version=$version",
                ("$image", imageId), ("$concept", concept), ("$source", source), ("$version", version));

        var retainedRejections = change.Rejections.Select(item => (item.ConceptId, item.SourceScope)).ToHashSet();
        var removedRejections = new List<(string Concept, string Scope)>();
        using (var query = database.Command("SELECT concept_id,source_scope FROM image_classification_rejections WHERE image_id=$image"))
        {
            query.Transaction = transaction; query.Parameters.AddWithValue("$image", imageId);
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                var key = (reader.GetString(0), reader.GetString(1));
                if (!retainedRejections.Contains(key)) removedRejections.Add(key);
            }
        }
        foreach (var (concept, scope) in removedRejections)
            Execute(database, transaction, "DELETE FROM image_classification_rejections WHERE image_id=$image AND concept_id=$concept AND source_scope=$scope",
                ("$image", imageId), ("$concept", concept), ("$scope", scope));
    }

    private static long[] ReadOwnedIds(PicerDatabase database, Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string publisher, string kind, string table, string parentColumn, long parentId)
    {
        using var query = database.Command($"""
            SELECT CAST(i.local_key AS INTEGER)
            FROM sync_entity_identities i
            JOIN {table} child ON child.id=CAST(i.local_key AS INTEGER)
            WHERE i.publisher_id=$publisher AND i.kind=$kind AND child.{parentColumn}=$parent
            """);
        query.Transaction = transaction;
        query.Parameters.AddWithValue("$publisher", publisher);
        query.Parameters.AddWithValue("$kind", kind);
        query.Parameters.AddWithValue("$parent", parentId);
        using var reader = query.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids.ToArray();
    }

    private static void Execute(PicerDatabase database, Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string sql, params (string Name, object Value)[] parameters)
    {
        using var command = database.Command(sql);
        command.Transaction = transaction;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }
}
