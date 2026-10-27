using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    public void PromoteUnversionedMetadata(SqliteTransaction? externalTransaction=null)
    {
        const string eligible="e.server_version=0 AND (e.kind NOT IN ('video-tag-link','video-concept') OR EXISTS(SELECT 1 FROM video_owned_metadata_edits owned WHERE owned.operation_id=e.operation_id))";
        using (var pending=Command("SELECT 1 FROM metadata_edits e WHERE "+eligible+" LIMIT 1"))
        {
            pending.Transaction=externalTransaction;
            if (pending.ExecuteScalar() is null) return;
        }
        using var ownedTransaction=externalTransaction is null?BeginTransaction():null;
        var transaction=externalTransaction??ownedTransaction!;
        using var promote=Command($"""
            UPDATE metadata_version_clock SET version=MAX(version,(SELECT COALESCE(MAX(MAX(server_version,base_version)),0) FROM metadata_edits)) WHERE id=1;
            CREATE TEMP TABLE IF NOT EXISTS metadata_version_promotion(row_id INTEGER PRIMARY KEY,version INTEGER NOT NULL);
            DELETE FROM temp.metadata_version_promotion;
            INSERT INTO temp.metadata_version_promotion
            SELECT e.rowid,(SELECT version FROM metadata_version_clock WHERE id=1)+ROW_NUMBER() OVER(ORDER BY e.kind,e.entity_id,e.field)
            FROM metadata_edits e WHERE {eligible};
            UPDATE metadata_edits SET server_version=(SELECT version FROM temp.metadata_version_promotion WHERE row_id=metadata_edits.rowid)
            WHERE rowid IN (SELECT row_id FROM temp.metadata_version_promotion);
            UPDATE metadata_version_clock SET version=MAX(version,COALESCE((SELECT MAX(version) FROM temp.metadata_version_promotion),0)) WHERE id=1;
            DELETE FROM temp.metadata_version_promotion;
            """);
        promote.Transaction=transaction; promote.ExecuteNonQuery(); ownedTransaction?.Commit();
    }

    public IReadOnlyList<MetadataEdit> ReadVersionedMetadataEdits(string publisher,long after,string operation,long through,int limit,SqliteTransaction transaction)
    {
        using var query=Command("""
            SELECT e.kind,e.entity_id,e.field,e.value,e.updated_utc,e.operation_id,e.video_content_identity,e.server_version,e.base_version
            FROM metadata_edits e
            WHERE (e.server_version>$after OR (e.server_version=$after AND e.operation_id>$operation)) AND e.server_version<=$through
                AND (e.kind='space-setting' OR EXISTS(SELECT 1 FROM sync_entity_identities i WHERE i.publisher_id=$publisher
                    AND i.kind=CASE e.kind WHEN 'person' THEN 'person' WHEN 'face-state' THEN 'face' WHEN 'face-rejection' THEN 'face' WHEN 'video-face-state' THEN 'video-face' WHEN 'video-face-rejection' THEN 'video-face' WHEN 'scene-description' THEN 'image' WHEN 'tag-link' THEN 'image' WHEN 'photo-classification-confirmation' THEN 'image' WHEN 'photo-classification-policy' THEN 'image' WHEN 'album-link' THEN 'album' ELSE 'video' END
                    AND i.target_id=e.entity_id)
                    OR (e.kind IN ('photo-classification-confirmation','photo-classification-policy') AND EXISTS(
                        SELECT 1 FROM photo_metadata_operation_bindings b JOIN sync_entity_identities i
                            ON i.kind='image' AND i.target_id=b.reference_id AND i.publisher_id=$publisher
                        WHERE b.operation_id=e.operation_id)))
                AND (e.kind NOT IN ('video-tag-link','video-concept') OR EXISTS(
                    SELECT 1 FROM video_owned_metadata_edits owned WHERE owned.operation_id=e.operation_id))
                AND NOT EXISTS(SELECT 1 FROM video_metadata_ownership_reviews r WHERE r.operation_id=e.operation_id)
                AND (e.kind NOT IN ('video-face-state','video-face-rejection') OR EXISTS(
                    SELECT 1 FROM video_effective_faces f WHERE f.id=e.entity_id AND f.model_version=json_extract(e.value,'$.ModelVersion')))
            ORDER BY e.server_version,e.operation_id LIMIT $limit
            """);
        query.Transaction=transaction; query.Parameters.AddWithValue("$publisher",publisher); query.Parameters.AddWithValue("$after",after);
        query.Parameters.AddWithValue("$operation",operation); query.Parameters.AddWithValue("$through",through); query.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,501));
        var result=new List<MetadataEdit>();
        using var rows=query.ExecuteReader();
        while (rows.Read()) result.Add(new(rows.GetString(0),rows.GetInt64(1),rows.GetString(2),rows.GetString(3),rows.GetString(4),rows.GetString(5))
        { VideoContentIdentity=rows.IsDBNull(6)?null:rows.GetString(6),ServerVersion=rows.GetInt64(7),BaseVersion=rows.GetInt64(8) });
        rows.Close();
        return result.Select(edit=>NormalizeCausalPhotoEdit(edit,transaction)).ToArray();
    }
}
