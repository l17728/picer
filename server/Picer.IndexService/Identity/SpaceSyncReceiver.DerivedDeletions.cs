using System.Text.Json;
using Picer.Core.Identity;

namespace Picer.IndexService.Identity;

public sealed partial class SpaceSyncReceiver
{
    private int ApplyDerivedDeletions(string publisher,DerivedMetadataDeletionBatch batch)
    {
        if(batch.Items is null || batch.Items.Count>100 || batch.Scope is null) throw new ArgumentException("invalid_derived_deletions");
        ValidateAdoptionPublisher(publisher,SpaceEpoch(),batch.Scope);
        var accepted=0;
        foreach(var item in batch.Items)
        {
            if(item is null || !Guid.TryParseExact(item.OperationId,"N",out _) || !Guid.TryParseExact(item.MediaGlobalId,"N",out _) ||
                item.Before.ValueKind!=JsonValueKind.Object || DerivedMetadataDeletionRules.Get(item.Table) is not {} rule ||
                item.MediaKind!=(item.Table.StartsWith("video_",StringComparison.Ordinal)?"video":"image"))
                throw new ArgumentException("invalid_derived_deletions");
            if((item.MediaKind=="image"?!batch.Scope.Photos:!batch.Scope.Videos) ||
                (item.Table is "detected_faces" or "video_detected_faces" or "face_candidate_members" && !batch.Scope.People) ||
                (item.Table=="image_locations" && !batch.Scope.PreciseLocation)) throw new UnauthorizedAccessException();
            var mediaTable=item.MediaKind=="image"?"images":"videos";
            var image=Scalar($"""
                SELECT i.id FROM sync_entity_identities e JOIN {mediaTable} i ON i.id=e.target_id
                JOIN sync_source_bindings b ON b.publisher_id=e.publisher_id AND b.source_id=i.source_id
                WHERE e.publisher_id=$publisher AND e.kind=$kind AND e.global_id=$global
                    AND b.local_source_id IN(SELECT value FROM json_each($sources))
                """,("$publisher",publisher),("$global",item.MediaGlobalId),("$kind",item.MediaKind),("$sources",JsonSerializer.Serialize(batch.Scope.SourceIds)));
            if(image is null) throw new UnauthorizedAccessException();
            var content=item.MediaKind=="video"?Scalar("SELECT content_id FROM videos WHERE id=$id",("$id",image)):image;
            var sharedOutsideScope=item.MediaKind=="video" && Scalar("""
                SELECT 1 FROM videos v WHERE v.content_id=$content AND NOT EXISTS(
                    SELECT 1 FROM sync_source_bindings b WHERE b.publisher_id=$publisher AND b.source_id=v.source_id
                        AND b.local_source_id IN(SELECT value FROM json_each($sources))) LIMIT 1
                """,("$content",content),("$publisher",publisher),("$sources",JsonSerializer.Serialize(batch.Scope.SourceIds))) is not null;
            using var remove=Sql($"DELETE FROM {item.Table} WHERE "+string.Join(" AND ",rule.Columns.Select((column,index)=>$"{column} IS $v{index}")));
            if(DerivedMetadataDeletionRules.EntityKind(item.Table) is {} entityKind)
            {
                if(!Guid.TryParseExact(item.EntityGlobalId,"N",out _)) throw new ArgumentException("invalid_derived_deletions");
                remove.CommandText+=$" AND {(item.Table=="face_candidate_members"?"face_id":"id")}=(SELECT target_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind=$entityKind AND global_id=$entityGlobal)";
                remove.Parameters.AddWithValue("$publisher",publisher);remove.Parameters.AddWithValue("$entityKind",entityKind);remove.Parameters.AddWithValue("$entityGlobal",item.EntityGlobalId);
            }
            for(var index=0;index<rule.Columns.Length;index++)
            {
                var column=rule.Columns[index];
                if(!item.Before.TryGetProperty(column,out var value)) throw new ArgumentException("invalid_derived_deletions");
                object? mapped=value.ValueKind switch { JsonValueKind.Null=>null,JsonValueKind.String=>value.GetString(),JsonValueKind.Number=>value.TryGetInt64(out var number)?(object)number:value.GetDouble(),_=>throw new ArgumentException("invalid_derived_deletions") };
                if(column=="image_id") mapped=image;
                if(column=="video_id") mapped=content;
                if(column=="similarity" && item.Table=="semantic_content_cluster_members") mapped=(double)value.GetSingle();
                if(column=="cluster_key") mapped=publisher+":"+value.GetString();
                if(column=="concept_id" && value.GetString() is {} concept && concept.StartsWith("cluster.semantic.",StringComparison.Ordinal))
                    mapped="cluster.semantic."+publisher+":"+concept["cluster.semantic.".Length..];
                remove.Parameters.AddWithValue("$v"+index,mapped??DBNull.Value);
            }
            // Machine recomputation must never revoke a human decision.
            if(item.Table=="image_classification_assertions")
                remove.CommandText+=" AND review_state='pending' AND source_kind IN ('vision_model','semantic_cluster','vl_description')";
            if(item.Table=="detected_faces")
                remove.CommandText+="""
                     AND NOT EXISTS(SELECT 1 FROM face_person_assignments a WHERE a.face_id=detected_faces.id)
                     AND NOT EXISTS(SELECT 1 FROM face_person_rejections r WHERE r.face_id=detected_faces.id)
                     AND NOT EXISTS(SELECT 1 FROM face_review_states r WHERE r.face_id=detected_faces.id)
                     AND NOT EXISTS(SELECT 1 FROM metadata_edits e WHERE e.kind IN ('face-state','face-rejection') AND e.entity_id=detected_faces.id)
                    """;
            if(item.Table=="video_detected_faces")
            {
                remove.CommandText+="""
                     AND owner_key=$owner AND assigned_person_id IS NULL AND assignment_state IS NULL
                     AND NOT EXISTS(SELECT 1 FROM video_face_person_rejections r WHERE r.video_face_id=video_detected_faces.id)
                     AND NOT EXISTS(SELECT 1 FROM metadata_edits e WHERE e.kind IN ('video-face-state','video-face-rejection') AND e.entity_id=video_detected_faces.id)
                    """;
                remove.Parameters.AddWithValue("$owner","publisher:"+publisher);
            }
            if(item.Table=="video_keyframes")
                remove.CommandText+=" AND NOT EXISTS(SELECT 1 FROM video_detected_faces f WHERE f.keyframe_id=video_keyframes.id)";
            if(item.Table=="face_candidate_members")
            {
                if(!Guid.TryParseExact(item.RelatedGlobalId,"N",out _) || !item.Before.TryGetProperty("model_version",out var model) || model.ValueKind!=JsonValueKind.String)
                    throw new ArgumentException("invalid_derived_deletions");
                remove.CommandText+="""
                     AND cluster_id=(SELECT target_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind='face-cluster' AND global_id=$cluster)
                     AND EXISTS(SELECT 1 FROM detected_faces f JOIN face_candidate_clusters c ON c.id=face_candidate_members.cluster_id
                         WHERE f.id=face_candidate_members.face_id AND f.image_id=$image AND f.model_version=$model AND c.model_version=$model)
                    """;
                remove.Parameters.AddWithValue("$cluster",item.RelatedGlobalId);remove.Parameters.AddWithValue("$image",image);remove.Parameters.AddWithValue("$model",model.GetString());
            }
            var deleted=sharedOutsideScope?0:remove.ExecuteNonQuery();accepted+=deleted;
            if(deleted>0 && item.Table=="semantic_content_cluster_members")
                Execute("""
                    UPDATE semantic_content_clusters SET member_count=(SELECT COUNT(*) FROM semantic_content_cluster_members WHERE cluster_key=$key) WHERE cluster_key=$key;
                    DELETE FROM semantic_content_clusters WHERE cluster_key=$key AND NOT EXISTS(SELECT 1 FROM semantic_content_cluster_members WHERE cluster_key=$key);
                    """,("$key",publisher+":"+item.Before.GetProperty("cluster_key").GetString()));
            if(deleted>0 && item.Table=="face_candidate_members")
                Execute("""
                    DELETE FROM face_candidate_clusters WHERE id=(SELECT target_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind='face-cluster' AND global_id=$cluster)
                        AND NOT EXISTS(SELECT 1 FROM face_candidate_members WHERE cluster_id=face_candidate_clusters.id);
                    """,("$publisher",publisher),("$cluster",item.RelatedGlobalId));
            // Even a retained newer result must be returned to the source that
            // removed its local copy. Application suppresses ordinary feed triggers.
            Execute("""
                INSERT INTO sync_metadata_changes(publisher_id,global_image_id,changed_utc,media_kind)
                VALUES($publisher,$global,$now,$kind);
                INSERT INTO sync_publisher_notifications(publisher_id,revision) VALUES($publisher,1)
                ON CONFLICT(publisher_id) DO UPDATE SET revision=revision+1;
                """,("$publisher",publisher),("$global",item.MediaGlobalId),("$kind",item.MediaKind),("$now",DateTime.UtcNow.ToString("O")));
        }
        return accepted;
    }
}
