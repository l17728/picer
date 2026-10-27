using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class PhotoClassificationMetadataMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction=connection.BeginTransaction();using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="""
            DROP TRIGGER IF EXISTS content_owned_face_cache_purge;
            CREATE TABLE IF NOT EXISTS sync_photo_content_identities(
                publisher_id TEXT NOT NULL REFERENCES sync_publishers(publisher_id) ON DELETE CASCADE,
                source_identity TEXT NOT NULL,reference_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
                content_identity TEXT NOT NULL,source_sha256 TEXT,
                PRIMARY KEY(publisher_id,source_identity));
            CREATE INDEX IF NOT EXISTS idx_sync_photo_content_target ON sync_photo_content_identities(publisher_id,reference_id,content_identity);
            DROP TRIGGER IF EXISTS photo_metadata_file_version_update;
            CREATE TRIGGER photo_metadata_file_version_update AFTER UPDATE OF file_size,modified_utc,sha256 ON images
            WHEN NEW.file_size IS NOT OLD.file_size OR NEW.modified_utc IS NOT OLD.modified_utc
                OR (length(OLD.sha256)=64 AND lower(OLD.sha256) NOT GLOB '*[^0-9a-f]*' AND lower(NEW.sha256) IS NOT lower(OLD.sha256)) BEGIN
                UPDATE photo_metadata_file_versions SET version_id=lower(hex(randomblob(16))) WHERE image_id=NEW.id;
            END;
            CREATE INDEX IF NOT EXISTS idx_photo_metadata_binding_content ON photo_metadata_operation_bindings(owner_key,content_identity);
            DROP TRIGGER IF EXISTS photo_metadata_binding_purge;
            CREATE TRIGGER photo_metadata_binding_purge BEFORE DELETE ON images BEGIN
                UPDATE photo_metadata_operation_bindings SET reference_id=COALESCE((
                    SELECT MIN(i.id) FROM images i JOIN sync_image_owners o ON o.image_id=i.id
                    WHERE i.id<>OLD.id AND o.owner_key=photo_metadata_operation_bindings.owner_key
                      AND 'sha256:'||lower(i.sha256)=photo_metadata_operation_bindings.content_identity),reference_id)
                WHERE reference_id=OLD.id;
                DELETE FROM sync_imported_metadata_edits WHERE entity_kind IN ('photo-classification-confirmation','photo-classification-policy')
                    AND operation_id IN (SELECT operation_id FROM photo_metadata_operation_bindings WHERE reference_id=OLD.id);
                DELETE FROM metadata_edit_conflicts WHERE kind IN ('photo-classification-confirmation','photo-classification-policy')
                    AND operation_id IN (SELECT operation_id FROM photo_metadata_operation_bindings WHERE reference_id=OLD.id);
                DELETE FROM metadata_causal_receipts WHERE kind IN ('photo-classification-confirmation','photo-classification-policy')
                    AND operation_id IN (SELECT operation_id FROM photo_metadata_operation_bindings WHERE reference_id=OLD.id);
                DELETE FROM metadata_edits WHERE kind IN ('photo-classification-confirmation','photo-classification-policy')
                    AND operation_id IN (SELECT operation_id FROM photo_metadata_operation_bindings WHERE reference_id=OLD.id);
                DELETE FROM photo_metadata_operation_bindings WHERE reference_id=OLD.id;
            END;
            PRAGMA user_version=75;
            """;
        command.ExecuteNonQuery();
        foreach(var operation in new[]{"INSERT","UPDATE"})
        {
            var changed=operation=="UPDATE"?" AND (NEW.operation_id IS NOT OLD.operation_id OR NEW.value IS NOT OLD.value)":"";
            command.CommandText=$"""
                CREATE TRIGGER IF NOT EXISTS photo_classification_field_notify_{operation} AFTER {operation} ON metadata_edits
                WHEN NEW.kind IN ('photo-classification-confirmation','photo-classification-policy')
                  AND NOT EXISTS(SELECT 1 FROM sync_suppression WHERE id=1){changed}
                BEGIN
                    UPDATE mirror_revision SET metadata_revision=metadata_revision+1 WHERE id=1;
                    INSERT INTO sync_publisher_notifications(publisher_id,revision)
                    SELECT DISTINCT publisher_id,1 FROM sync_entity_identities WHERE kind='image' AND target_id=NEW.entity_id
                    ON CONFLICT(publisher_id) DO UPDATE SET revision=revision+1;
                END;
                """;
            command.ExecuteNonQuery();
        }
        PhotoClassificationLegacySeed.Apply(connection,transaction);
        command.CommandText=CaptureHashConflicts("1=1");command.ExecuteNonQuery();
        command.CommandText="SELECT sql FROM sqlite_master WHERE type='trigger' AND name='photo_metadata_bind_verified_hash'";
        var hashTrigger=(string)command.ExecuteScalar()!;
        var ending=hashTrigger.LastIndexOf("END",StringComparison.Ordinal);
        if(ending<0) throw new InvalidDataException("photo_hash_binding_trigger_invalid");
        command.CommandText="DROP TRIGGER photo_metadata_bind_verified_hash";command.ExecuteNonQuery();
        command.CommandText=hashTrigger[..ending]+CaptureHashConflicts()+" END";command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static string CaptureHashConflicts(string filter="b.content_identity='sha256:'||lower(NEW.sha256) AND b.owner_key=(SELECT owner_key FROM sync_image_owners WHERE image_id=NEW.id)")
    {
        static string Json(string alias)=>$"""
            json_object('Kind',{alias}.kind,'EntityId',{alias}.entity_id,'Field',{alias}.field,'Value',{alias}.value,
                'UpdatedUtc',{alias}.updated_utc,'OperationId',{alias}.operation_id,'ServerVersion',{alias}.server_version,
                'BaseVersion',{alias}.base_version,'PhotoContentIdentity',{alias}.content_identity)
            """;
        return $"""
            INSERT INTO metadata_edit_conflicts(operation_id,kind,entity_id,field,proposed_json,current_json)
            WITH candidates AS (
                SELECT e.*,b.owner_key,b.content_identity,ROW_NUMBER() OVER(PARTITION BY e.kind,e.field,b.owner_key,b.content_identity
                    ORDER BY MAX(e.server_version,e.base_version) DESC,(e.server_version=0) DESC,e.updated_utc DESC,e.operation_id DESC) AS position
                FROM photo_metadata_operation_bindings b JOIN metadata_edits e ON e.operation_id=b.operation_id
                WHERE {filter}
                    AND e.kind IN ('photo-classification-confirmation','photo-classification-policy'))
            SELECT p.operation_id,p.kind,p.entity_id,p.field,{Json("p")},{Json("w")}
            FROM candidates p JOIN candidates w ON w.kind=p.kind AND w.field=p.field AND w.position=1
                AND w.owner_key=p.owner_key AND w.content_identity=p.content_identity
            WHERE p.position>1 AND p.value<>w.value
            ON CONFLICT(operation_id) DO NOTHING;
            """;
    }
}
