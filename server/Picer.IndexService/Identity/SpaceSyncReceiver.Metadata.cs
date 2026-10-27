using System.Text.Json;
using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Identity;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Video;

namespace Picer.IndexService.Identity;

public sealed partial class SpaceSyncReceiver
{
    public SpaceMetadataEditPage ReadMetadataEditPage(long after,string operation,long? through,int pageSize,string expectedEpoch)
    {
        if (!device.Allows("index.read")) throw new UnauthorizedAccessException();
        if (after<0 || pageSize is <1 or >500 || operation is not ("" or "~") && !Guid.TryParseExact(operation,"N",out _) ||
            !Guid.TryParseExact(expectedEpoch,"N",out _)) throw new ArgumentException("invalid_metadata_cursor");
        using var transaction=database.BeginTransaction();
        var publisher=Scalar("SELECT publisher_id FROM sync_publishers WHERE incarnation_id=$device AND device_grant_id=$grant AND space_id=$space AND state='receiving'",
            ("$device",device.DeviceId),("$grant",device.GrantId),("$space",device.SpaceId)) as string ?? throw new UnauthorizedAccessException();
        var epoch=Scalar("SELECT epoch FROM sync_publishers WHERE publisher_id=$publisher",("$publisher",publisher)) as string;
        if (epoch!=expectedEpoch || epoch!=SpaceEpoch()) throw new InvalidOperationException("publisher_epoch_reset_required");
        database.PromoteUnversionedMetadata(transaction);
        var latest=Convert.ToInt64(Scalar("SELECT version FROM metadata_version_clock WHERE id=1"));
        var cut=through??latest;
        if (cut<after || cut>latest) throw new InvalidOperationException("metadata_change_baseline_required");
        var rows=database.ReadVersionedMetadataEdits(publisher,after,operation,cut,pageSize+1,transaction);
        var more=rows.Count>pageSize; var selected=rows.Take(pageSize).ToArray();
        var edits=selected.Select(edit=>TranslateMetadataEdit(publisher,edit)).OfType<MetadataEdit>().ToArray();
        var result=new SpaceMetadataEditPage(publisher,epoch!,cut,more?selected[^1].ServerVersion:cut,more?selected[^1].OperationId:"~",more,edits,latest);
        transaction.Commit(); return result;
    }

    public SpaceArchivedOperationResponse ReadArchivedOperations(SpaceArchivedOperationRequest request)
    {
        using var transaction = database.BeginReadTransaction();
        var (publisher,epoch)=ValidateOperationProbe(request);
        var archived = new List<string>();
        foreach (var operation in request.OperationIds.Distinct(StringComparer.Ordinal))
        {
            var remoteOperation = DeviceAuthorization.Digest(publisher + ":" + operation)[..32];
            if (Scalar("SELECT 1 FROM video_metadata_ownership_reviews WHERE operation_id=$op", ("$op", remoteOperation)) is not null ||
                Scalar("""
                    SELECT 1 FROM video_metadata_ownership_reviews r WHERE r.operation_id=$op AND EXISTS(
                        SELECT 1 FROM sync_entity_identities e JOIN videos v ON v.id=e.target_id AND e.kind='video'
                        WHERE e.publisher_id=$publisher AND v.content_id=r.content_id)
                    """, ("$op", operation), ("$publisher", publisher)) is not null)
                archived.Add(operation);
        }
        return new(publisher, epoch, archived);
    }

    public SpaceConflictResolutionResponse ReadConflictResolutions(SpaceArchivedOperationRequest request)
    {
        using var transaction=database.BeginReadTransaction();
        var (publisher,epoch)=ValidateOperationProbe(request);
        var resolutions=new List<MetadataConflictReceipt>();
        foreach (var operation in request.OperationIds.Distinct(StringComparer.Ordinal))
        {
            // Only the originating publisher may probe an operation. Do not
            // accept arbitrary service IDs from another source or remote GUI.
            var remote=DeviceAuthorization.Digest(publisher+":"+operation)[..32];
            if (Scalar("SELECT resolved_version FROM metadata_edit_conflicts WHERE operation_id=$op AND state='resolved' AND resolution_json IS NOT NULL AND resolved_version>0",("$op",remote)) is long version)
                resolutions.Add(new(operation,version));
        }
        return new(publisher,epoch,resolutions);
    }

    private (string Publisher,string Epoch) ValidateOperationProbe(SpaceArchivedOperationRequest request)
    {
        if (!device.Allows("index.read") || !device.Allows("index.write")) throw new UnauthorizedAccessException();
        if (request.OperationIds is null || request.OperationIds.Count > 500 ||
            request.OperationIds.Any(id => !Guid.TryParseExact(id, "N", out _)))
            throw new ArgumentException("invalid_operation_probe");
        var publisher = Scalar("SELECT publisher_id FROM sync_publishers WHERE publisher_id=$publisher AND incarnation_id=$device AND device_grant_id=$grant AND space_id=$space AND state='receiving'",
            ("$publisher", request.PublisherId), ("$device", device.DeviceId), ("$grant", device.GrantId), ("$space", device.SpaceId)) as string
            ?? throw new UnauthorizedAccessException();
        var epoch = Scalar("SELECT epoch FROM sync_publishers WHERE publisher_id=$publisher", ("$publisher", publisher)) as string;
        if (request.Epoch != epoch || epoch != SpaceEpoch()) throw new InvalidOperationException("publisher_epoch_reset_required");
        return (publisher,epoch!);
    }

    public SpaceMetadataOperationResponse ReadMetadataOperationStates(SpaceArchivedOperationRequest request)
    {
        using var transaction=database.BeginTransaction();
        var (publisher,epoch)=ValidateOperationProbe(request);
        database.PromoteUnversionedMetadata(transaction);
        var states=new List<SpaceMetadataOperationState>();
        foreach (var operation in request.OperationIds.Distinct(StringComparer.Ordinal))
        {
            var remote=DeviceAuthorization.Digest(publisher+":"+operation)[..32];
            if (database.ReadCurrentMetadataOperation(remote,transaction) is { ServerVersion: >0 } current &&
                TranslateMetadataEdit(publisher,current) is { } translated)
                states.Add(new(operation,translated));
        }
        transaction.Commit(); return new(publisher,epoch,states);
    }

    private MetadataEdit? TranslateMetadataEdit(string publisher,MetadataEdit edit)
    {
        if (edit.Kind=="space-setting") return edit;
        long? Local(string kind,long id)
        {
            var key=Scalar("SELECT local_key FROM sync_active_entity_identities WHERE publisher_id=$publisher AND kind=$kind AND target_id=$id",
                ("$publisher",publisher),("$kind",kind),("$id",id));
            return key is string value && long.TryParse(value,out var local)?local:null;
        }
        var kind=edit.Kind switch { "person"=>"person","face-state" or "face-rejection"=>"face","video-face-state" or "video-face-rejection"=>"video-face","scene-description" or "tag-link" or "photo-classification-confirmation" or "photo-classification-policy"=>"image","album-link"=>"album","video-tag-link" or "video-concept"=>"video",_=>"" };
        if (kind.Length==0 || Local(kind,edit.EntityId) is not { } entity) return null;
        if(kind=="video-face" && Scalar("SELECT 1 FROM video_effective_faces WHERE id=$id AND owner_key='publisher:'||$publisher LIMIT 1",
            ("$id",edit.EntityId),("$publisher",publisher)) is null) return null;
        var field=edit.Field;
        if(edit.Kind=="scene-description")
        {
            if(!SceneOwnedByPublisher(publisher,edit.Field) || database.GetSceneLocalFingerprint(publisher,edit.Field) is not {} localScene)return null;
            field=localScene;
        }
        if(IsPhotoEdit(edit))
        {
            var prefix="cluster.semantic."+publisher+":";
            if(field.StartsWith(prefix,StringComparison.Ordinal)) field="cluster.semantic."+field[prefix.Length..];
        }
        if (edit.Kind is "tag-link" or "video-tag-link" or "album-link")
        {
            if (!long.TryParse(field,out var related) || Local(edit.Kind=="album-link"?"image":"tag",related) is not { } localRelated) return null;
            field=localRelated.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var value=edit.Value;
        if(edit.Kind is "face-rejection" or "video-face-rejection")
        {
            if(!long.TryParse(field,out var person) || Local("person",person) is not { } localPerson) return null;
            field=localPerson.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if(edit.Kind is "face-state" or "video-face-state")
        {
            var state=PicerDatabase.ParseFaceManualState(value);
            if(state.PersonId is { } person)
            {
                if(Local("person",person) is not { } localPerson) return null;
                state=state with { PersonId=localPerson };
            }
            value=JsonSerializer.Serialize(state);
        }
        return ExportVideoContentIdentity(publisher, edit with { EntityId=entity,Field=field,Value=value });
    }

    public SpaceMetadataEditSnapshot ReadMetadataEdits()
    {
        if (!device.Allows("index.read")) throw new UnauthorizedAccessException("metadata_sync_read_denied");
        database.PromoteClusterPreference();
        using var snapshot = database.BeginReadTransaction();
        var publisher = Scalar("SELECT publisher_id FROM sync_publishers WHERE incarnation_id=$device AND device_grant_id=$grant AND space_id=$space AND state='receiving'",
            ("$device", device.DeviceId), ("$grant", device.GrantId), ("$space", device.SpaceId)) as string
            ?? throw new UnauthorizedAccessException("publisher_unavailable");
        var epoch = Scalar("SELECT epoch FROM sync_publishers WHERE publisher_id=$publisher", ("$publisher", publisher)) as string
            ?? throw new InvalidOperationException("publisher_unavailable");
        if (epoch != SpaceEpoch()) throw new InvalidOperationException("publisher_epoch_reset_required");
        long? Local(string kind, long remoteId)
        {
            var key = Scalar("SELECT local_key FROM sync_active_entity_identities WHERE publisher_id=$publisher AND kind=$kind AND target_id=$id",
                ("$publisher", publisher), ("$kind", kind), ("$id", remoteId));
            return key is string value && long.TryParse(value, out var id) ? id : null;
        }
        var edits = new List<Picer.Core.Data.MetadataEdit>();
        foreach (var edit in database.GetMetadataEdits(snapshot))
        {
            if(edit.Kind is "scene-description" or "face-state" or "face-rejection" or "video-face-state" or "video-face-rejection" or "photo-classification-confirmation" or "photo-classification-policy") { if(TranslateMetadataEdit(publisher,edit) is { } translatedEdit) edits.Add(translatedEdit);continue; }
            if (edit.Kind=="space-setting") { edits.Add(edit); continue; }
            var kind = edit.Kind switch
            {
                "person" => "person", "tag-link" => "image", "album-link" => "album",
                "video-tag-link" or "video-concept" => "video", _ => ""
            };
            if (kind.Length == 0 || Local(kind, edit.EntityId) is not { } localEntity) continue;
            var localField = edit.Field;
            if (edit.Kind is "tag-link" or "video-tag-link")
            {
                if (!long.TryParse(edit.Field, out var remoteTag) || Local("tag", remoteTag) is not { } localTag) continue;
                localField = localTag.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (edit.Kind == "album-link")
            {
                if (!long.TryParse(edit.Field, out var remoteImage) || Local("image", remoteImage) is not { } localImage) continue;
                localField = localImage.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (ExportVideoContentIdentity(publisher, edit with { EntityId = localEntity, Field = localField }) is { } translated)
                edits.Add(translated);
        }
        if (edits.Count > 20_000) throw new InvalidOperationException("metadata_edit_snapshot_too_large");
        return new(edits, publisher, epoch);
    }

    public SpaceMetadataChangePage ReadMetadataChanges(long after, int pageSize)
    {
        if (!device.Allows("index.read") || after < 0) throw new UnauthorizedAccessException("classification_sync_read_denied");
        // Keep the event waterline, identities and row snapshots in the same WAL read
        // transaction; a concurrent edit must never be acknowledged without being read.
        using var snapshot = database.BeginReadTransaction();
        var publisher = Scalar("SELECT publisher_id FROM sync_publishers WHERE incarnation_id=$device AND device_grant_id=$grant AND space_id=$space AND state='receiving'",
            ("$device", device.DeviceId), ("$grant", device.GrantId), ("$space", device.SpaceId)) as string
            ?? throw new UnauthorizedAccessException("publisher_unavailable");
        var epoch = Scalar("SELECT epoch FROM sync_publishers WHERE publisher_id=$publisher", ("$publisher", publisher)) as string
            ?? throw new InvalidOperationException("publisher_unavailable");
        if (epoch != SpaceEpoch()) throw new InvalidOperationException("publisher_epoch_reset_required");
        var size = Math.Clamp(pageSize, 1, 200);
        var pageRows = new List<(long ChangeId, string MediaKind, string GlobalId)>();
        using (var query = Sql("SELECT MAX(change_id),media_kind,global_image_id FROM sync_metadata_changes WHERE publisher_id=$publisher GROUP BY media_kind,global_image_id HAVING MAX(change_id)>$after ORDER BY MAX(change_id) LIMIT $limit",
            ("$publisher", publisher), ("$after", after), ("$limit", size + 1)))
        using (var reader = query.ExecuteReader())
            while (reader.Read()) pageRows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        var more = pageRows.Count > size;
        var selected = pageRows.Take(size).ToArray();
        var changes = new List<SpaceMetadataChange>(selected.Length);
        foreach (var (changeId, mediaKind, globalId) in selected)
        {
            var identityKind = mediaKind == "video" ? "video" : "image";
            var target = Scalar("SELECT target_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind=$kind AND global_id=$global",
                ("$publisher", publisher), ("$kind", identityKind), ("$global", globalId));
            if (target is null) continue;
            var mediaId = Convert.ToInt64(target);
            long? LocalId(string kind, long remoteId)
            {
                var key = Scalar("SELECT local_key FROM sync_active_entity_identities WHERE publisher_id=$publisher AND kind=$kind AND target_id=$id",
                    ("$publisher", publisher), ("$kind", kind), ("$id", remoteId));
                return key is string value && long.TryParse(value, out var localId) ? localId : null;
            }
            if (mediaKind == "video")
            {
                var localVideoId = LocalId("video", mediaId);
                if (localVideoId is null) continue;
                var summaries = ReadRows("""
                    SELECT (SELECT MIN(id) FROM videos WHERE content_id=s.video_id),s.provider,s.model_version,s.prompt_version,s.summary,s.result_json,s.source_file_size,s.source_modified_utc,s.created_utc
                    FROM video_summaries s WHERE s.video_id=(SELECT content_id FROM videos WHERE id=$image)
                    """, mediaId, row => new GatewayVideoSummary(row.GetInt64(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetString(4),
                        row.GetString(5), row.IsDBNull(6) ? null : row.GetInt64(6), row.IsDBNull(7) ? null : row.GetString(7)) { UpdatedUtc = row.GetString(8) });
                var transcripts = ReadRows("""
                    SELECT (SELECT MIN(id) FROM videos WHERE content_id=t.video_id),t.model_version,t.transcript_json,t.created_utc
                    FROM video_transcripts t WHERE t.video_id=(SELECT content_id FROM videos WHERE id=$image)
                    """, mediaId, row => new GatewayVideoTranscript(row.GetInt64(0), row.GetString(1), row.GetString(2)) { UpdatedUtc = row.GetString(3) });
                var keyframes = ReadRows("""
                    SELECT id,timestamp_seconds,path,dhash FROM video_keyframes
                    WHERE video_id=(SELECT content_id FROM videos WHERE id=$image) ORDER BY timestamp_seconds,id
                    """, mediaId, row => (Remote: row.GetInt64(0), Local: LocalId("keyframe", row.GetInt64(0)),
                        Time: row.GetDouble(1), Path: row.GetString(2), Hash: row.GetInt64(3)))
                    .Where(item => item.Local is not null).Select(item => new GatewayVideoKeyframe(item.Local!.Value, localVideoId.Value,
                        item.Time, PublishedMediaPath.TryUnqualify(item.Path, out var ownerId, out var originalPath) && ownerId == publisher ? originalPath : string.Empty,
                        item.Hash)).ToArray();
                var localKeyframeIds = keyframes.Select(item => item.Id).ToHashSet();
                var unmappedVideoPeople = false;
                var videoFaces = ReadRows<(long RemoteId, long? LocalId, long RemoteKeyframe, long? LocalKeyframe, int Ordinal,
                    double Timestamp, string Model, double X, double Y, double Width, double Height, double Confidence, long? Suggested,
                    double? Match, long? Assigned, string? State, string? Updated, string Created)>("""
                    SELECT id,keyframe_id,timestamp_seconds,ordinal,model_version,box_x,box_y,box_width,box_height,confidence,
                           suggested_person_id,match_confidence,assigned_person_id,assignment_state,updated_utc,created_utc
                    FROM video_detected_faces WHERE video_id=(SELECT content_id FROM videos WHERE id=$image)
                        AND owner_key=(SELECT owner_key FROM sync_video_owners WHERE video_id=$image) ORDER BY timestamp_seconds,id
                    """, mediaId, row =>
                {
                    var remoteKeyframe = row.GetInt64(1);
                    var suggested = row.IsDBNull(10) ? null : LocalId("person", row.GetInt64(10));
                    var assigned = row.IsDBNull(12) ? null : LocalId("person", row.GetInt64(12));
                    if (!row.IsDBNull(10) && suggested is null || !row.IsDBNull(12) && assigned is null) unmappedVideoPeople = true;
                    return (row.GetInt64(0), LocalId("video-face", row.GetInt64(0)), remoteKeyframe, LocalId("keyframe", remoteKeyframe),
                        row.GetInt32(3), row.GetDouble(2), row.GetString(4), row.GetDouble(5), row.GetDouble(6), row.GetDouble(7), row.GetDouble(8),
                        row.GetDouble(9), suggested, row.IsDBNull(11) ? (double?)null : row.GetDouble(11),
                        assigned, row.IsDBNull(13) ? null : row.GetString(13),
                        row.IsDBNull(14) ? null : row.GetString(14), row.GetString(15));
                }).Where(item => item.LocalId is not null && item.LocalKeyframe is not null && localKeyframeIds.Contains(item.LocalKeyframe.Value))
                    .Select(item => new GatewayVideoFace(item.LocalId!.Value, localVideoId.Value, item.LocalKeyframe!.Value, item.Timestamp, item.Ordinal,
                        item.X, item.Y, item.Width, item.Height, item.Confidence, null, item.Suggested, item.Match, item.Assigned, item.State, item.Updated, item.Created)
                    { ModelVersion = item.Model }).ToArray();
                changes.Add(new(changeId, globalId, [], [], [], [], "video", VideoSummaries: summaries, VideoTranscripts: transcripts,
                    VideoKeyframes: keyframes, VideoFaces: unmappedVideoPeople ? null : videoFaces));
                continue;
            }
            var imageId = mediaId;
            var assertions = ReadRows("""
                SELECT image_id,content_sha256,concept_id,source_kind,source_version,assertion_state,review_state,confidence,
                       evidence_fingerprint,created_utc,updated_utc,reviewed_utc
                FROM image_classification_assertions WHERE image_id=$image ORDER BY concept_id,source_kind,source_version
                """, imageId, row => new GatewayClassificationAssertion(row.GetInt64(0), row.IsDBNull(1) ? null : row.GetString(1),
                    UnnamespaceConcept(publisher, row.GetString(2)), row.GetString(3), row.GetString(4), row.GetString(5), row.GetString(6),
                    row.IsDBNull(7) ? null : row.GetFloat(7), row.IsDBNull(8) ? null : row.GetString(8), row.GetString(9), row.GetString(10),
                    row.IsDBNull(11) ? null : row.GetString(11)));
            var rejections = ReadRows("SELECT $image,concept_id,source_scope,rejected_utc FROM image_classification_rejections WHERE image_id=$image OR (content_sha256=(SELECT lower(COALESCE(content_sha256,sha256)) FROM images WHERE id=$image) AND owner_key=(SELECT owner_key FROM sync_image_owners WHERE image_id=$image)) ORDER BY concept_id,source_scope",
                imageId, row => new GatewayClassificationRejection(row.GetInt64(0), UnnamespaceConcept(publisher, row.GetString(1)), row.GetString(2), row.GetString(3)));
            var clusters = ReadRows("""
                SELECT DISTINCT c.cluster_key,c.model_version,c.display_name,c.confidence,c.member_count,c.created_utc,c.updated_utc
                FROM semantic_content_clusters c JOIN semantic_content_cluster_members m ON m.cluster_key=c.cluster_key
                WHERE m.image_id=$image AND c.cluster_key LIKE $prefix ORDER BY c.cluster_key
                """, imageId, row => new GatewaySemanticContentCluster(UnnamespaceCluster(publisher, row.GetString(0)), row.GetString(1),
                    row.GetString(2), row.GetFloat(3), row.GetInt32(4), row.GetString(5), row.GetString(6)), publisher + ":%");
            var members = ReadRows("""
                SELECT m.cluster_key,m.image_id,m.similarity FROM semantic_content_cluster_members m
                WHERE m.image_id=$image AND m.cluster_key LIKE $prefix ORDER BY m.cluster_key
                """, imageId, row => new GatewaySemanticContentClusterMember(UnnamespaceCluster(publisher, row.GetString(0)),
                    row.GetInt64(1), row.GetFloat(2)), publisher + ":%");
            var analyses = ReadRows("""
                SELECT image_id,provider,model_version,prompt_version,status,summary,result_json,updated_utc,source_file_size,source_modified_utc
                FROM cloud_vision_analyses WHERE image_id=$image AND status='completed' ORDER BY provider
                """, imageId, row => new GatewayCloudVisionAnalysis(row.GetInt64(0), row.GetString(1), row.GetString(2), row.GetString(3),
                    row.GetString(4), row.IsDBNull(5) ? null : row.GetString(5), row.IsDBNull(6) ? null : row.GetString(6), row.GetString(7),
                    row.IsDBNull(8) ? null : row.GetInt64(8), row.IsDBNull(9) ? null : row.GetString(9)));
            var localImageKey = LocalId("image", imageId);
            if (localImageKey is null) continue;
            var localImageId = localImageKey.Value;
            var faces = ReadRows("""
                SELECT id,image_id,model_version,ordinal,box_x,box_y,box_width,box_height,confidence,landmarks_json,created_utc,is_archived
                FROM detected_faces WHERE image_id=$image ORDER BY id
                """, imageId, row => (Remote: row.GetInt64(0), Face: new GatewayDetectedFace(
                    LocalId("face", row.GetInt64(0)) ?? 0, localImageId, row.GetString(2), row.GetInt32(3), row.GetDouble(4), row.GetDouble(5),
                    row.GetDouble(6), row.GetDouble(7), row.GetDouble(8), row.IsDBNull(9) ? null : row.GetString(9), null, row.GetString(10), row.GetBoolean(11))))
                .Where(item => item.Face.Id > 0).Select(item => item.Face).ToArray();
            var faceMap = ReadRows("SELECT id FROM detected_faces WHERE image_id=$image", imageId, row =>
                (Remote: row.GetInt64(0), Local: LocalId("face", row.GetInt64(0)))).Where(item => item.Local is not null)
                .ToDictionary(item => item.Remote, item => item.Local!.Value);
            var candidateRows=ReadRows("""
                SELECT c.id,c.model_version,c.created_utc,m.face_id,m.similarity FROM face_candidate_clusters c
                JOIN face_candidate_members m ON m.cluster_id=c.id JOIN detected_faces f ON f.id=m.face_id
                WHERE f.image_id=$image AND f.model_version=c.model_version
                """,imageId,row=>(Cluster:LocalId("face-cluster",row.GetInt64(0)),Model:row.GetString(1),Created:row.GetString(2),Face:row.GetInt64(3),Similarity:row.GetDouble(4)))
                .Where(item=>item.Cluster is not null && faceMap.ContainsKey(item.Face)).ToArray();
            var candidateClusters=candidateRows.GroupBy(item=>item.Cluster!.Value).Select(group=>
                new GatewayFaceCandidateCluster(group.Key,group.First().Model,group.First().Created)).ToArray();
            var candidateMembers=candidateRows.Select(item=>new GatewayFaceCandidateMember(item.Cluster!.Value,faceMap[item.Face],item.Similarity)).ToArray();
            var assignmentRows = ReadRows<(long RemoteFace, long RemotePerson, long? LocalPerson, string State, double? Confidence, string AssignedBy, string Updated)>("""
                SELECT a.face_id,a.person_id,a.state,a.confidence,a.assigned_by,a.updated_utc
                FROM face_person_assignments a JOIN detected_faces f ON f.id=a.face_id WHERE f.image_id=$image
                """, imageId, row => (RemoteFace: row.GetInt64(0), RemotePerson: row.GetInt64(1), LocalPerson: LocalId("person", row.GetInt64(1)),
                    State: row.GetString(2), Confidence: row.IsDBNull(3) ? (double?)null : row.GetDouble(3), AssignedBy: row.GetString(4), Updated: row.GetString(5)));
            IReadOnlyList<GatewayFaceAssignment>? assignments = assignmentRows.All(item => faceMap.ContainsKey(item.RemoteFace) && item.LocalPerson is not null)
                ? assignmentRows
                .Select(item => new GatewayFaceAssignment(faceMap[item.RemoteFace], item.LocalPerson!.Value,
                    item.State, item.Confidence, item.AssignedBy, item.Updated)).ToArray() : null;
            var decisions = ReadRows("""
                WITH decisions AS (
                    SELECT c.face_id,CASE WHEN c.action IN ('ignore','sync_review_ignored') THEN 'ignored' ELSE 'active' END AS state,c.created_utc
                    FROM face_corrections c JOIN detected_faces f ON f.id=c.face_id
                    WHERE f.image_id=$image AND c.action IN ('ignore','move_to_unassigned','move','create_and_move',
                        'not_this_person','merge_person','review_active','sync_review_ignored','sync_review_active')
                ), ranked AS (
                    SELECT *,ROW_NUMBER() OVER(PARTITION BY face_id ORDER BY created_utc DESC,state DESC) AS rank FROM decisions
                ) SELECT face_id,state,created_utc FROM ranked WHERE rank=1
                """, imageId, row => new GatewayFaceReviewDecision(row.GetInt64(0), row.GetString(1), row.GetString(2)))
                .Where(item => faceMap.ContainsKey(item.FaceId)).Select(item => item with { FaceId = faceMap[item.FaceId] }).ToArray();
            var locations = ReadRows("""
                SELECT CASE WHEN i.gps IS NOT NULL AND i.gps<>'' THEN 1 ELSE 0 END,
                    l.country,l.province,l.city,l.county,l.district,l.town,l.village
                FROM image_locations l JOIN images i ON i.id=l.image_id WHERE i.id=$image
                """, imageId, row => new ImageLocationHierarchyRow(localImageId, row.GetInt64(0) == 1,
                    row.IsDBNull(1) ? null : row.GetString(1), row.IsDBNull(2) ? null : row.GetString(2), row.IsDBNull(3) ? null : row.GetString(3),
                    row.IsDBNull(4) ? null : row.GetString(4), row.IsDBNull(5) ? null : row.GetString(5), row.IsDBNull(6) ? null : row.GetString(6),
                    row.IsDBNull(7) ? null : row.GetString(7)));
            changes.Add(new(changeId, globalId, assertions, rejections, clusters, members, "image", analyses,
                Faces: faces, FaceAssignments: assignments, FaceReviewDecisions: decisions, Locations: locations,
                FaceCandidateClusters:candidateClusters,FaceCandidateMembers:candidateMembers,SceneDescriptions:ReadPublisherScenes(publisher,imageId)));
        }
        long through;
        using (var latest = Sql("SELECT COALESCE(MAX(change_id),0) FROM sync_metadata_changes WHERE publisher_id=$publisher", ("$publisher", publisher)))
            through = Convert.ToInt64(latest.ExecuteScalar());
        if (more) through = selected[^1].ChangeId;
        if (after > through) throw new InvalidOperationException("metadata_change_baseline_required");
        return new(through, more, changes, publisher, epoch);

        string UnnamespaceCluster(string id, string value) => value.StartsWith(id + ":", StringComparison.Ordinal) ? value[(id.Length + 1)..] : value;
        string UnnamespaceConcept(string id, string value)
        {
            var prefix = "cluster.semantic." + id + ":";
            return value.StartsWith(prefix, StringComparison.Ordinal) ? "cluster.semantic." + value[prefix.Length..] : value;
        }
        IReadOnlyList<T> ReadRows<T>(string sql, long image, Func<SqliteDataReader, T> decode, string? prefix = null)
        {
            using var command = Sql(sql, ("$image", image), ("$prefix", (object?)prefix ?? DBNull.Value));
            using var reader = command.ExecuteReader();
            var rows = new List<T>();
            while (reader.Read()) rows.Add(decode(reader));
            return rows;
        }
    }

    private int ApplyMetadataEdits(string publisher, SpaceMetadataEditsBatch batch, Dictionary<(string, long), long> identities, SqliteTransaction transaction)
    {
        if (batch.Edits is null || batch.Edits.Count > 500) throw new ArgumentException("invalid_metadata_edits");
        var catalogGenerations=new Dictionary<string,string>(StringComparer.Ordinal);
        var edits = batch.Edits.Where(edit=>!ReferencesRetiredCatalog(publisher,edit)).Select(edit =>
        {
            PicerDatabase.ValidateMetadataEdit(edit);
            if (edit.Kind=="space-setting") return edit with { OperationId=DeviceAuthorization.Digest(publisher+":"+edit.OperationId)[..32] };
            var kind = edit.Kind switch { "person" => "person", "face-state" or "face-rejection" => "face", "video-face-state" or "video-face-rejection"=>"video-face", "scene-description" or "tag-link" or "photo-classification-confirmation" or "photo-classification-policy" => "image", "album-link" => "album", "video-tag-link" or "video-concept" => "video", _ => throw new ArgumentException("invalid_metadata_kind") };
            var field = edit.Kind switch
            {
                "scene-description" => database.GetSceneTargetFingerprint(publisher,edit.Field) ?? throw new ArgumentException("unpublished_scene"),
                "tag-link" or "video-tag-link" => Resolve(publisher, identities, "tag", long.Parse(edit.Field, System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "album-link" => Resolve(publisher, identities, "image", long.Parse(edit.Field, System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "photo-classification-confirmation" or "photo-classification-policy" when edit.Field.StartsWith("cluster.semantic.",StringComparison.Ordinal)
                    =>"cluster.semantic."+publisher+":"+edit.Field["cluster.semantic.".Length..],
                _ => edit.Field
            };
            var target = Resolve(publisher, identities, kind, edit.EntityId);
            if(edit.Kind=="scene-description" && !SceneOwnedByPublisher(publisher,field,target))throw new ArgumentException("scene_source_mismatch");
            var value=edit.Value;
            var faceTable=kind=="video-face"?"video_effective_faces":"detected_faces";
            if(edit.Kind is "face-rejection" or "video-face-rejection")
            {
                var rejection=PicerDatabase.ParseFacePersonRejection(value);
                if(Scalar($"SELECT model_version FROM {faceTable} WHERE id=$id",("$id",target)) as string!=rejection.ModelVersion)
                    throw new ArgumentException("face_model_mismatch");
                field=Resolve(publisher,identities,"person",long.Parse(edit.Field,System.Globalization.CultureInfo.InvariantCulture))
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
                value=JsonSerializer.Serialize(rejection);
            }
            if(edit.Kind is "face-state" or "video-face-state")
            {
                var state=PicerDatabase.ParseFaceManualState(value);
                if(Scalar($"SELECT model_version FROM {faceTable} WHERE id=$id",("$id",target)) as string!=state.ModelVersion)
                    throw new ArgumentException("face_model_mismatch");
                if(state.PersonId is { } person) state=state with { PersonId=Resolve(publisher,identities,"person",person) };
                value=JsonSerializer.Serialize(state);
            }
            var translated=edit with { EntityId = target, Field = field, Value=value, VideoContentIdentity = ImportVideoContentIdentity(publisher, edit, target),
                PhotoContentIdentity=ImportPhotoContentIdentity(publisher,edit,target),
                OperationId = DeviceAuthorization.Digest(publisher + ":" + edit.OperationId)[..32] };
            if(FrozenCatalogForEdit(edit) is { } global) catalogGenerations[translated.OperationId]=global;
            return translated;
        }).ToArray();
        var beforeVersion=Convert.ToInt64(Scalar("SELECT version FROM metadata_version_clock WHERE id=1"));
        var accepted=database.AcceptMetadataEditsInTransaction(edits, transaction,new(publisher,catalogGenerations));
        foreach (var edit in accepted.Where(edit=>edit.ServerVersion>beforeVersion && edit.Kind is "person" or "face-state" or "face-rejection" or "video-face-state" or "video-face-rejection" or "tag-link" or "album-link" or "space-setting" or "photo-classification-confirmation" or "photo-classification-policy"))
        {
            // An origin upload is suppressed to prevent echo, but another source
            // owning the same explicitly adopted catalog object needs a signal.
            // A content hash alone never authorizes cross-publisher delivery.
            var owners=edit.Kind switch
            {
                "space-setting"=>"SELECT publisher_id FROM sync_publishers WHERE state='receiving'",
                "person"=>"SELECT publisher_id FROM sync_entity_identities WHERE kind='person' AND target_id=$id",
                "face-state" or "face-rejection"=>"SELECT publisher_id FROM sync_entity_identities WHERE kind='face' AND target_id=$id",
                "video-face-state" or "video-face-rejection"=>"SELECT publisher_id FROM sync_entity_identities WHERE kind='video-face' AND target_id=$id",
                "scene-description" or "tag-link" or "photo-classification-confirmation" or "photo-classification-policy"=>"SELECT publisher_id FROM sync_entity_identities WHERE kind='image' AND target_id=$id",
                "album-link"=>"SELECT publisher_id FROM sync_entity_identities WHERE kind='image' AND target_id=CAST($field AS INTEGER)",
                _=>throw new InvalidOperationException("unsupported_metadata_notification")
            };
            Execute($"""
                INSERT INTO sync_publisher_notifications(publisher_id,revision)
                SELECT DISTINCT publisher_id,1 FROM ({owners}) WHERE publisher_id<>$origin
                ON CONFLICT(publisher_id) DO UPDATE SET revision=revision+1
                """,("$id",edit.EntityId),("$field",edit.Field),("$origin",publisher));
        }
        return edits.Length;
    }
    private long Resolve(string publisher, Dictionary<(string, long), long> identities, string kind, long id)
    {
        if(_catalogBatchReferences is not null && _catalogBatchReferences.TryGetValue((kind,id),out var expected))
        {
            var actual=Scalar("SELECT global_id FROM sync_active_entity_identities WHERE publisher_id=$publisher AND kind=$kind AND local_key=$local",
                ("$publisher",publisher),("$kind",kind),("$local",id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if(actual as string!=expected) throw new ArgumentException("global_identity_changed");
        }
        if (identities.TryGetValue((kind, id), out var mapped)) return mapped;
        var value = Scalar("SELECT target_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind=$kind AND local_key=$local",
            ("$publisher", publisher), ("$kind", kind), ("$local", id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return value is null ? throw new ArgumentException("unpublished_reference") : Convert.ToInt64(value);
    }

    private void Rebind(string publisher, Dictionary<(string, long), long> identities, string kind, long local, long canonical)
    {
        Execute("UPDATE sync_entity_identities SET target_id=$canonical WHERE publisher_id=$publisher AND kind=$kind AND local_key=$local",
            ("$canonical", canonical), ("$publisher", publisher), ("$kind", kind), ("$local", local.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        identities[(kind, local)] = canonical;
    }

    private int ApplyCatalog(string publisher, SpaceCatalogBatch batch, Dictionary<(string, long), long> identities)
    {
        if (batch.Tags is null || batch.Albums is null || batch.People is null || batch.Tags.Count + batch.Albums.Count + batch.People.Count > 1000) throw new ArgumentException("catalog_batch_too_large");
        foreach (var tag in batch.Tags)
        {
            if(IsRetiredCatalogReference(publisher,"tag",tag.Id)) continue;
            var local = tag.Id;
            var id = Resolve(publisher, identities, "tag", local);
            var canonical = Scalar("SELECT id FROM tags WHERE name=$name", ("$name", tag.Name));
            if (canonical is not null && Convert.ToInt64(canonical) != id)
            { id = Convert.ToInt64(canonical); Rebind(publisher, identities, "tag", local, id); }
            tag.Id = id;
            database.UpsertMirroredTag(tag);
        }
        foreach (var album in batch.Albums)
        {
            if(IsRetiredCatalogReference(publisher,"album",album.Id)) continue;
            album.Id = Resolve(publisher, identities, "album", album.Id);
            if (album.CreatorId is { } source)
            {
                var target = Scalar("SELECT source_id FROM sync_source_bindings WHERE publisher_id=$publisher AND local_source_id=$source", ("$publisher", publisher), ("$source", source));
                album.CreatorId = target is null ? null : Convert.ToInt64(target);
            }
            database.UpsertMirroredAlbum(album);
        }
        foreach (var person in batch.People)
        {
            if(IsRetiredCatalogReference(publisher,"person",person.Id)) continue;
            var id = Resolve(publisher, identities, "person", person.Id);
            var name = person.DisplayName;
            if (string.IsNullOrWhiteSpace(name) || name.Length > 150) throw new ArgumentException("invalid_person_name");
            // Equal display names are not evidence of equal people. Preserve both until the user explicitly merges them.
            if (Scalar("SELECT id FROM people WHERE trim(display_name)=trim($name) COLLATE NOCASE AND id<>$id", ("$name", name), ("$id", id)) is not null)
                name += "（来源 " + publisher + "）";
            database.UpsertMirroredPerson(person with { Id = id, DisplayName = name });
        }
        return batch.Tags.Count + batch.Albums.Count + batch.People.Count;
    }

    private int ApplyMetadata(string publisher, SpaceMetadataBatch batch, Dictionary<(string, long), long> identities, SqliteTransaction transaction)
    {
        var data = batch.Metadata;
        if (data is null || data.Faces is null || data.FaceAssignments is null || data.AlbumImages is null || data.ImageTags is null ||
            data.Locations is null || data.VideoKeyframes is null || data.VideoTranscripts is null || data.VideoSummaries is null)
            throw new ArgumentException("invalid_metadata");
        var count = data.Faces.Count + data.FaceAssignments.Count + data.AlbumImages.Count + data.ImageTags.Count + data.Locations.Count +
            data.VideoKeyframes.Count + data.VideoFacesOrEmpty.Count + data.CloudVisionAnalysesOrEmpty.Count + data.VideoSummaries.Count +
            data.VideoTranscripts.Count + data.SceneGroupDescriptionsOrEmpty.Count + data.FaceReviewStatesOrEmpty.Count +
            data.FaceCandidateClustersOrEmpty.Count + data.FaceCandidateMembersOrEmpty.Count + data.FaceTrainingArtifactsOrEmpty.Count +
            data.FacePersonPoliciesOrEmpty.Count + data.FacePersonPrototypesOrEmpty.Count + data.FaceModelSettingsOrEmpty.Count +
            data.ClassificationAssertionsOrEmpty.Count + data.ClassificationRejectionsOrEmpty.Count +
            data.SemanticContentClustersOrEmpty.Count + data.SemanticContentClusterMembersOrEmpty.Count + (data.FaceReviewDecisions?.Count ?? 0);
        if (count > 2000) throw new ArgumentException("metadata_batch_too_large");
        long Map(string kind, long id) => Resolve(publisher, identities, kind, id);
        long? OptionalPerson(long? id) => id is null ? null : Map("person", id.Value);
        foreach (var face in data.Faces)
        {
            var image = Map("image", face.ImageId);
            var faceId = Map("face", face.Id);
            var canonical = Scalar("SELECT id FROM detected_faces WHERE image_id=$image AND model_version=$model AND ordinal=$ordinal",
                ("$image", image), ("$model", face.ModelVersion), ("$ordinal", face.Ordinal));
            if (canonical is not null && Convert.ToInt64(canonical) != faceId)
            { faceId = Convert.ToInt64(canonical); Rebind(publisher, identities, "face", face.Id, faceId); }
            database.UpsertMirroredFace(face with { Id = faceId, ImageId = image }, transaction);
        }
        foreach (var assignment in data.FaceAssignments.Where(item=>!IsRetiredCatalogReference(publisher,"person",item.PersonId)))
            database.UpsertMirroredFaceAssignment(assignment with { FaceId = Map("face", assignment.FaceId), PersonId = Map("person", assignment.PersonId) }, transaction);
        foreach (var link in data.AlbumImages.Where(item=>!IsRetiredCatalogReference(publisher,"album",item.AlbumId)))
            Execute("INSERT OR IGNORE INTO album_images(album_id,image_id,added_utc) VALUES($album,$image,$now)",
                ("$album", Map("album", link.AlbumId)), ("$image", Map("image", link.ImageId)), ("$now", DateTime.UtcNow.ToString("O")));
        foreach (var link in data.ImageTags.Where(item=>!IsRetiredCatalogReference(publisher,"tag",item.TagId)))
            database.UpsertMirroredImageTag(Map("image", link.ImageId), Map("tag", link.TagId));
        foreach (var location in data.Locations) database.UpsertMirroredLocation(location with { ImageId = Map("image", location.ImageId) }, transaction);

        foreach (var assertion in data.ClassificationAssertionsOrEmpty)
        {
            var concept = assertion.ConceptId.StartsWith("cluster.semantic.", StringComparison.Ordinal)
                ? "cluster.semantic." + publisher + ":" + assertion.ConceptId["cluster.semantic.".Length..]
                : assertion.ConceptId;
            Execute("""
                INSERT INTO image_classification_assertions(
                    image_id,content_sha256,concept_id,source_kind,source_version,assertion_state,review_state,confidence,
                    evidence_fingerprint,created_utc,updated_utc,reviewed_utc)
                VALUES($image,$sha,$concept,$source,$version,$assertion,$review,$confidence,$fingerprint,$created,$updated,$reviewed)
                ON CONFLICT(image_id,concept_id,source_kind,source_version) DO UPDATE SET
                    content_sha256=excluded.content_sha256,assertion_state=excluded.assertion_state,
                    review_state=CASE
                        WHEN image_classification_assertions.review_state IN ('accepted','rejected')
                             AND excluded.review_state='pending' THEN image_classification_assertions.review_state
                        WHEN excluded.updated_utc>=image_classification_assertions.updated_utc THEN excluded.review_state
                        ELSE image_classification_assertions.review_state END,
                    confidence=CASE WHEN excluded.updated_utc>=image_classification_assertions.updated_utc THEN excluded.confidence ELSE image_classification_assertions.confidence END,
                    evidence_fingerprint=CASE WHEN excluded.updated_utc>=image_classification_assertions.updated_utc THEN excluded.evidence_fingerprint ELSE image_classification_assertions.evidence_fingerprint END,
                    updated_utc=MAX(image_classification_assertions.updated_utc,excluded.updated_utc),
                    reviewed_utc=CASE WHEN excluded.updated_utc>=image_classification_assertions.updated_utc THEN excluded.reviewed_utc ELSE image_classification_assertions.reviewed_utc END
                """, ("$image", Map("image", assertion.ImageId)), ("$sha", assertion.ContentSha256),
                ("$concept", concept), ("$source", assertion.SourceKind), ("$version", assertion.SourceVersion),
                ("$assertion", assertion.AssertionState), ("$review", assertion.ReviewState), ("$confidence", assertion.Confidence),
                ("$fingerprint", assertion.EvidenceFingerprint), ("$created", assertion.CreatedUtc), ("$updated", assertion.UpdatedUtc),
                ("$reviewed", assertion.ReviewedUtc));
        }
        foreach (var rejection in data.ClassificationRejectionsOrEmpty)
            Execute("""
                INSERT INTO image_classification_rejections(image_id,content_sha256,concept_id,source_scope,rejected_utc)
                VALUES($image,NULL,$concept,$scope,$rejected)
                ON CONFLICT(image_id,concept_id,source_scope) WHERE image_id IS NOT NULL
                DO UPDATE SET rejected_utc=MAX(image_classification_rejections.rejected_utc,excluded.rejected_utc)
                """, ("$image", Map("image", rejection.ImageId)), ("$concept", rejection.ConceptId.StartsWith("cluster.semantic.",StringComparison.Ordinal)
                    ? "cluster.semantic."+publisher+":"+rejection.ConceptId["cluster.semantic.".Length..] : rejection.ConceptId),
                ("$scope", rejection.SourceScope), ("$rejected", rejection.RejectedUtc));

        string ClusterKey(string localKey) => publisher + ":" + localKey;
        foreach (var cluster in data.SemanticContentClustersOrEmpty)
            Execute("""
                INSERT INTO semantic_content_clusters(cluster_key,model_version,display_name,confidence,member_count,created_utc,updated_utc)
                VALUES($key,$model,$name,$confidence,$count,$created,$updated)
                ON CONFLICT(cluster_key) DO UPDATE SET model_version=excluded.model_version,display_name=excluded.display_name,
                    confidence=excluded.confidence,member_count=excluded.member_count,updated_utc=MAX(semantic_content_clusters.updated_utc,excluded.updated_utc)
                WHERE excluded.updated_utc>=semantic_content_clusters.updated_utc
                """, ("$key", ClusterKey(cluster.ClusterKey)), ("$model", cluster.ModelVersion), ("$name", cluster.DisplayName),
                ("$confidence", cluster.Confidence), ("$count", cluster.MemberCount), ("$created", cluster.CreatedUtc), ("$updated", cluster.UpdatedUtc));
        foreach (var member in data.SemanticContentClusterMembersOrEmpty)
            Execute("""
                INSERT INTO semantic_content_cluster_members(cluster_key,image_id,similarity)
                VALUES($key,$image,$similarity)
                ON CONFLICT(cluster_key,image_id) DO UPDATE SET similarity=excluded.similarity
                """, ("$key", ClusterKey(member.ClusterKey)), ("$image", Map("image", member.ImageId)), ("$similarity", member.Similarity));

        var frames = new List<GatewayVideoKeyframe>();
        foreach (var frame in data.VideoKeyframes)
        {
            var video = Map("video", frame.VideoId);
            var target = Map("keyframe", frame.Id);
            var content = new VideoContentRepository(database).GetContentId(video);
            var canonical = Scalar("SELECT id FROM video_keyframes WHERE video_id=$content AND timestamp_seconds=$time ORDER BY id LIMIT 1", ("$content", content), ("$time", frame.TimestampSeconds));
            if (canonical is not null && Convert.ToInt64(canonical) != target)
            { target = Convert.ToInt64(canonical); Rebind(publisher, identities, "keyframe", frame.Id, target); }
            // Adopted frames intentionally have no source-machine cache path.
            // Empty is a valid metadata-only reference, not a filesystem path.
            frames.Add(frame with { Id = target, VideoId = video,
                Path = frame.Path==string.Empty?string.Empty:PublishedMediaPath.Qualify(publisher, frame.Path) });
        }
        string MapImageArray(string json)
        {
            var ids = JsonSerializer.Deserialize<long[]>(json) ?? throw new ArgumentException("invalid_scene_members");
            if (ids.Length > 100_000) throw new ArgumentException("scene_too_large");
            return JsonSerializer.Serialize(ids.Select(id => Map("image", id)));
        }
        var videoFaces = new List<GatewayVideoFace>();
        foreach (var face in data.VideoFacesOrEmpty)
        {
            // Keep the current face state instead of replaying an old assignment
            // or suggestion to a retired person from a frozen snapshot.
            if(face.AssignedPersonId is { } assigned && IsRetiredCatalogReference(publisher,"person",assigned)
                || face.SuggestedPersonId is { } suggested && IsRetiredCatalogReference(publisher,"person",suggested)) continue;
            var video = Map("video", face.VideoId);
            var keyframe = Map("keyframe", face.KeyframeId);
            var target = Map("video-face", face.Id);
            var content = new VideoContentRepository(database).GetContentId(video);
            var owner=database.GetVideoOwnerKey(video,transaction);
            var canonical = Scalar("SELECT id FROM video_detected_faces WHERE video_id=$content AND keyframe_id=$keyframe AND ordinal=$ordinal AND model_version=$model AND owner_key=$owner ORDER BY id LIMIT 1",
                ("$content", content), ("$keyframe", keyframe), ("$ordinal", face.Ordinal), ("$model", face.ModelVersion),("$owner",owner));
            if (canonical is not null && Convert.ToInt64(canonical) != target)
            { target = Convert.ToInt64(canonical); Rebind(publisher, identities, "video-face", face.Id, target); }
            else if(Scalar("SELECT owner_key FROM video_detected_faces WHERE id=$id",("$id",target)) is string previousOwner && previousOwner!=owner)
            {
                target=Convert.ToInt64(Scalar("SELECT MAX(COALESCE((SELECT MAX(id) FROM video_detected_faces),0),COALESCE((SELECT MAX(target_id) FROM sync_entity_identities WHERE kind='video-face'),0),COALESCE((SELECT seq FROM sqlite_sequence WHERE name='video_detected_faces'),0))+1"));
                Rebind(publisher,identities,"video-face",face.Id,target);
            }
            videoFaces.Add(face with { Id = target, VideoId = video, KeyframeId = keyframe,
                SuggestedPersonId = OptionalPerson(face.SuggestedPersonId), AssignedPersonId = OptionalPerson(face.AssignedPersonId) });
        }
        var media = new GatewayMetadata([], [], [], [], [], frames,
            data.VideoTranscripts.Select(item => item with { VideoId = Map("video", item.VideoId) }).ToArray(),
            data.VideoSummaries.Select(item => item with { VideoId = Map("video", item.VideoId) }).ToArray(),
            videoFaces,
            data.CloudVisionAnalysesOrEmpty.Select(item => item with { ImageId = Map("image", item.ImageId) }).ToArray(),
            data.SceneGroupDescriptionsOrEmpty.Select(item => item with
            {
                Fingerprint = BindSceneFingerprint(publisher,item.Fingerprint), DirectoryPath = PublishedMediaPath.Qualify(publisher, item.DirectoryPath),
                ImageIdsJson = MapImageArray(item.ImageIdsJson), RepresentativeImageIdsJson = MapImageArray(item.RepresentativeImageIdsJson)
            }).ToArray());
        database.UpsertMirroredVideoMetadata(media, externalTransaction: transaction);
        foreach (var cluster in data.FaceCandidateClustersOrEmpty)
            Execute("INSERT INTO face_candidate_clusters(id,model_version,created_utc) VALUES($id,$model,$created) ON CONFLICT(id) DO UPDATE SET model_version=excluded.model_version",
                ("$id", Map("face-cluster", cluster.Id)), ("$model", cluster.ModelVersion), ("$created", cluster.CreatedUtc));
        foreach (var member in data.FaceCandidateMembersOrEmpty)
            Execute("INSERT INTO face_candidate_members(cluster_id,face_id,similarity) VALUES($cluster,$face,$similarity) ON CONFLICT(face_id) DO UPDATE SET cluster_id=excluded.cluster_id,similarity=excluded.similarity",
                ("$cluster", Map("face-cluster", member.ClusterId)), ("$face", Map("face", member.FaceId)), ("$similarity", member.Similarity));
        var decisions = (data.FaceReviewDecisions ?? []).Select(item => item with { FaceId = Map("face", item.FaceId) })
            .Concat(data.FaceReviewStatesOrEmpty.Select(item => new GatewayFaceReviewDecision(Map("face", item.FaceId), item.State, item.UpdatedUtc))).ToArray();
        database.MergeFaceReviewDecisionsInTransaction(decisions, transaction);
        ApplyTraining(publisher, data, Map);
        database.ReplayMetadataEditsInTransaction(transaction);
        return count;
    }

    private void ApplyTraining(string publisher, GatewayMetadata data, Func<string, long, long> map)
    {
        string Artifact(string id) => publisher + ":" + id;
        foreach (var artifact in data.FaceTrainingArtifactsOrEmpty)
        {
            var existingModel = Scalar("SELECT model_version FROM face_training_artifacts WHERE artifact_id=$id", ("$id", Artifact(artifact.ArtifactId))) as string;
            if (existingModel is not null && existingModel != artifact.ModelVersion) throw new ArgumentException("training_artifact_model_mismatch");
            if (!AcceptTrainingRow(publisher, "artifact", artifact.ArtifactId, artifact.ModelVersion,
                TrainingSyncBaseline.Artifact(artifact), existingModel is not null)) continue;
            if (artifact.State == "active")
                Execute("UPDATE face_training_artifacts SET state='superseded' WHERE endpoint=$endpoint AND model_version=$model AND state='active' AND artifact_id<>$id",
                    ("$endpoint", "source:" + publisher), ("$model", artifact.ModelVersion), ("$id", Artifact(artifact.ArtifactId)));
            Execute("""
                INSERT INTO face_training_artifacts(artifact_id,dataset_id,endpoint,model_version,artifact_json,false_accept_rate,false_reject_rate,state,created_utc,applied_utc)
                VALUES($id,$dataset,$endpoint,$model,$json,$far,$frr,$state,$created,$applied)
                ON CONFLICT(artifact_id) DO UPDATE SET artifact_json=excluded.artifact_json,false_accept_rate=excluded.false_accept_rate,
                false_reject_rate=excluded.false_reject_rate,state=excluded.state,applied_utc=excluded.applied_utc
                """, ("$id", Artifact(artifact.ArtifactId)), ("$dataset", Artifact(artifact.DatasetId)), ("$endpoint", "source:" + publisher),
                ("$model", artifact.ModelVersion), ("$json", artifact.ArtifactJson), ("$far", artifact.FalseAcceptRate), ("$frr", artifact.FalseRejectRate),
                ("$state", artifact.State), ("$created", artifact.CreatedUtc), ("$applied", artifact.AppliedUtc));
        }
        foreach (var prototype in data.FacePersonPrototypesOrEmpty.Where(item=>!IsRetiredCatalogReference(publisher,"person",item.PersonId)))
        {
            var person = map("person", prototype.PersonId);
            if (!AcceptTrainingRow(publisher, "prototype", TrainingSyncBaseline.PrototypeKey(prototype.PersonId, prototype.ModelVersion),
                prototype.ModelVersion, TrainingSyncBaseline.Row(prototype),
                Scalar("SELECT 1 FROM face_person_prototypes WHERE person_id=$person AND model_version=$model", ("$person", person), ("$model", prototype.ModelVersion)) is not null)) continue;
            Execute("""
                INSERT INTO face_person_prototypes(person_id,centroid,threshold,sample_count,model_version,updated_utc) VALUES($person,$centroid,$threshold,$count,$model,$updated)
                ON CONFLICT(person_id,model_version) DO UPDATE SET centroid=excluded.centroid,threshold=excluded.threshold,sample_count=excluded.sample_count,updated_utc=excluded.updated_utc
                """, ("$person", person), ("$centroid", prototype.Centroid), ("$threshold", prototype.Threshold),
                ("$count", prototype.SampleCount), ("$model", prototype.ModelVersion), ("$updated", prototype.UpdatedUtc));
        }
        foreach (var policy in data.FacePersonPoliciesOrEmpty.Where(item=>!IsRetiredCatalogReference(publisher,"person",item.PersonId)))
        {
            var person = map("person", policy.PersonId);
            if (!AcceptTrainingRow(publisher, "policy", TrainingSyncBaseline.PersonKey(policy.PersonId), policy.ModelVersion,
                TrainingSyncBaseline.Row(policy), Scalar("SELECT 1 FROM face_person_policies WHERE person_id=$person", ("$person", person)) is not null)) continue;
            Execute("""
                INSERT INTO face_person_policies(person_id,artifact_id,model_version,prototype,match_threshold,updated_utc) VALUES($person,$artifact,$model,$prototype,$threshold,$updated)
                ON CONFLICT(person_id) DO UPDATE SET artifact_id=excluded.artifact_id,model_version=excluded.model_version,prototype=excluded.prototype,match_threshold=excluded.match_threshold,updated_utc=excluded.updated_utc
                """, ("$person", person), ("$artifact", Artifact(policy.ArtifactId)), ("$model", policy.ModelVersion),
                ("$prototype", policy.Prototype), ("$threshold", policy.MatchThreshold), ("$updated", policy.UpdatedUtc));
        }
        foreach (var setting in data.FaceModelSettingsOrEmpty)
            Execute("""
                INSERT INTO face_model_settings(model_version,match_threshold,calibrated_same_pairs,calibrated_different_pairs,updated_utc) VALUES($model,$threshold,$same,$different,$updated)
                ON CONFLICT(model_version) DO NOTHING
                """, ("$model", setting.ModelVersion), ("$threshold", setting.MatchThreshold), ("$same", setting.CalibratedSamePairs),
                ("$different", setting.CalibratedDifferentPairs), ("$updated", setting.UpdatedUtc));
    }

    private bool AcceptTrainingRow(string publisher, string kind, string key, string model, string fingerprint, bool exists)
    {
        var previous = TrainingSyncBaseline.Read(database, publisher, kind, key);
        if (previous == fingerprint) return false;
        TrainingSyncBaseline.Save(database, publisher, kind, key, model, fingerprint);
        // The first baseline after an upgrade cannot prove an old source snapshot
        // supersedes a server application. Preserve the authoritative existing row.
        return previous is not null || !exists;
    }
}
