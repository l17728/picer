using System.Text.Json;
using Picer.Core.Data;

namespace Picer.Core.Identity;

public sealed partial class SpaceSyncPublisher
{
    private static void CompactDerivedJournal(PicerDatabase database,long cut)
    {
        // Upserts are carried by the committed snapshot. Explicit deletion evidence
        // survives until every publisher that knows this media has acknowledged it.
        // Bound the transaction so cleanup cannot monopolize the foreground database.
        using var cleanup=database.Command("""
            DELETE FROM sync_metadata_row_journal WHERE sequence IN (
                SELECT j.sequence FROM sync_metadata_row_journal j WHERE j.sequence<=$cut AND (
                    j.operation='upsert' OR (j.operation='delete'
                        AND EXISTS(SELECT 1 FROM sync_derived_deletion_acks a WHERE a.operation_id=j.operation_id)
                        AND NOT EXISTS(SELECT 1 FROM sync_publishers p JOIN sync_entity_identities e ON e.publisher_id=p.publisher_id
                            WHERE p.state<>'receiving' AND ((e.kind='image' AND e.local_key=CAST(json_extract(j.before_payload,'$.image_id') AS TEXT))
                                OR(e.kind='video' AND EXISTS(SELECT 1 FROM videos v WHERE CAST(v.id AS TEXT)=e.local_key AND v.content_id=json_extract(j.before_payload,'$.video_id'))))
                                AND NOT EXISTS(SELECT 1 FROM sync_derived_deletion_acks a WHERE a.publisher_id=p.publisher_id AND a.operation_id=j.operation_id))))
                ORDER BY j.sequence LIMIT 5000);
            """);
        cleanup.Parameters.AddWithValue("$cut",cut);cleanup.ExecuteNonQuery();
    }

    private static IEnumerable<ExportBatch> ExportDerivedDeletions(PicerDatabase snapshot,string publisher,SpaceUploadScope scope)
    {
        using(var prepare=snapshot.Command("CREATE TABLE IF NOT EXISTS sync_export_superseded_deletions(operation_id TEXT PRIMARY KEY)")) prepare.ExecuteNonQuery();
        using var query=snapshot.Command("""
            SELECT j.operation_id,j.table_name,j.before_payload,e.global_id,'image',j.sequence
            FROM sync_metadata_row_journal j
            JOIN images i ON i.id=json_extract(j.before_payload,'$.image_id')
            JOIN sync_entity_identities e ON e.publisher_id=$publisher AND e.kind='image' AND e.local_key=CAST(i.id AS TEXT)
            WHERE j.operation='delete' AND j.table_name IN
                ('image_classification_assertions','semantic_content_cluster_members','cloud_vision_analyses','image_locations','detected_faces','face_candidate_members')
                AND NOT EXISTS(SELECT 1 FROM sync_derived_deletion_acks a WHERE a.publisher_id=$publisher AND a.operation_id=j.operation_id)
            UNION ALL
            SELECT j.operation_id,j.table_name,j.before_payload,e.global_id,'video',j.sequence
            FROM sync_metadata_row_journal j
            JOIN videos v ON v.id=(SELECT MIN(id) FROM videos WHERE content_id=json_extract(j.before_payload,'$.video_id'))
            JOIN sync_entity_identities e ON e.publisher_id=$publisher AND e.kind='video' AND e.local_key=CAST(v.id AS TEXT)
            WHERE j.operation='delete' AND j.table_name IN('video_summaries','video_transcripts','video_keyframes','video_detected_faces')
                AND NOT EXISTS(SELECT 1 FROM sync_export_blocked_video_deletions b WHERE b.content_id=v.content_id)
                AND NOT EXISTS(SELECT 1 FROM sync_derived_deletion_acks a WHERE a.publisher_id=$publisher AND a.operation_id=j.operation_id)
            ORDER BY sequence
            """);
        query.Parameters.AddWithValue("$publisher",publisher);
        var pending=new List<DerivedMetadataDeletion>();
        using var rows=query.ExecuteReader();
        while(rows.Read())
        {
            var table=rows.GetString(1);var rule=DerivedMetadataDeletionRules.Get(table)!.Value;
            using var document=JsonDocument.Parse(rows.GetString(2));var before=document.RootElement;
            // Delete/reinsert within a recomputation is represented by the final upsert.
            using var current=snapshot.Command($"SELECT 1 FROM {table} WHERE "+string.Join(" AND ",rule.Keys.Select(key=>$"{key} IS json_extract($before,'$.{key}')"))+
                (table=="cloud_vision_analyses"?" AND status='completed'":"")+" LIMIT 1");
            current.Parameters.AddWithValue("$before",before.GetRawText());
            if(current.ExecuteScalar() is not null)
            {
                MarkSupersededDeletion(snapshot,rows.GetString(0));continue;
            }
            string? entityGlobal=null;string? relatedGlobal=null;
            if(DerivedMetadataDeletionRules.EntityKind(table) is {} entityKind)
            {
                using var identity=snapshot.Command("SELECT global_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind=$kind AND local_key=$id");
                identity.Parameters.AddWithValue("$publisher",publisher);identity.Parameters.AddWithValue("$kind",entityKind);
                identity.Parameters.AddWithValue("$id",before.GetProperty(table=="face_candidate_members"?"face_id":"id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture));
                entityGlobal=identity.ExecuteScalar() as string;
                if(entityGlobal is null) {MarkSupersededDeletion(snapshot,rows.GetString(0));continue;} // Never published by this device.
            }
            var payload=rule.Columns.ToDictionary(column=>column,column=>before.GetProperty(column).Clone());
            if(table=="face_candidate_members")
            {
                using var cluster=snapshot.Command("SELECT global_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind='face-cluster' AND local_key=$id");
                cluster.Parameters.AddWithValue("$publisher",publisher);cluster.Parameters.AddWithValue("$id",before.GetProperty("cluster_id").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture));
                relatedGlobal=cluster.ExecuteScalar() as string;if(relatedGlobal is null){MarkSupersededDeletion(snapshot,rows.GetString(0));continue;}
                payload["model_version"]=before.GetProperty("model_version").Clone();
            }
            pending.Add(new(rows.GetString(0),table,rows.GetString(3),JsonSerializer.SerializeToElement(payload),rows.GetString(4),entityGlobal,relatedGlobal));
            if(pending.Count==100) { yield return new("derived-deletions",new DerivedMetadataDeletionBatch(pending.ToArray(),scope),[]);pending.Clear(); }
        }
        if(pending.Count>0) yield return new("derived-deletions",new DerivedMetadataDeletionBatch(pending.ToArray(),scope),[]);
    }

    private static void AcknowledgeDerivedDeletions(PicerDatabase database,string publisher,SpaceSyncReceipt receipt)
    {
        if(receipt.Status!=200 || LoadPending(database,publisher) is not {Kind:"derived-deletions"} batch) return;
        var items=batch.Payload.Deserialize<DerivedMetadataDeletionBatch>(DeviceAuthorization.Json)!.Items;
        AcknowledgeDerivedOperations(database,publisher,items.Select(item=>item.OperationId));
    }

    private static void MarkSupersededDeletion(PicerDatabase snapshot,string operation)
    {
        using var command=snapshot.Command("INSERT OR IGNORE INTO sync_export_superseded_deletions VALUES($operation)");
        command.Parameters.AddWithValue("$operation",operation);command.ExecuteNonQuery();
    }

    private static void AcknowledgeDerivedOperations(PicerDatabase database,string publisher,IEnumerable<string> operations)
    {
        using var insert=database.Command("INSERT OR IGNORE INTO sync_derived_deletion_acks SELECT $publisher,operation_id FROM sync_metadata_row_journal WHERE operation_id=$operation");
        foreach(var operation in operations)
        {
            insert.Parameters.Clear();insert.Parameters.AddWithValue("$publisher",publisher);insert.Parameters.AddWithValue("$operation",operation);insert.ExecuteNonQuery();
        }
    }
}
