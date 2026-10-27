using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class FaceRejectionMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction=connection.BeginTransaction();using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="SELECT 1 FROM pragma_table_info('sync_imported_metadata_edits') WHERE name='entity_field'";
        if(command.ExecuteScalar() is null)
        {
            command.CommandText="ALTER TABLE sync_imported_metadata_edits ADD COLUMN entity_field TEXT";command.ExecuteNonQuery();
        }
        command.CommandText="""
            INSERT OR IGNORE INTO metadata_edits(kind,entity_id,field,value,updated_utc,operation_id)
            SELECT 'face-rejection',r.face_id,CAST(r.person_id AS TEXT),
                json_object('Rejected',json('true'),'ModelVersion',f.model_version),
                strftime('%Y-%m-%dT%H:%M:%f','now')||'0000Z',lower(hex(randomblob(16)))
            FROM face_person_rejections r JOIN detected_faces f ON f.id=r.face_id;
            CREATE TRIGGER IF NOT EXISTS space_sync_face_rejection_purge AFTER DELETE ON detected_faces BEGIN
                DELETE FROM sync_imported_metadata_edits WHERE entity_kind='face-rejection' AND entity_id=OLD.id;
                DELETE FROM metadata_edits WHERE kind='face-rejection' AND entity_id=OLD.id;
                DELETE FROM metadata_causal_receipts WHERE kind='face-rejection' AND entity_id=OLD.id;
                DELETE FROM metadata_edit_conflicts WHERE kind='face-rejection' AND entity_id=OLD.id;
            END;
            CREATE TRIGGER IF NOT EXISTS space_sync_person_rejection_purge BEFORE DELETE ON people BEGIN
                DELETE FROM sync_imported_metadata_edits WHERE entity_kind='face-rejection' AND entity_field=CAST(OLD.id AS TEXT);
                DELETE FROM metadata_edits WHERE kind='face-rejection' AND field=CAST(OLD.id AS TEXT);
                DELETE FROM metadata_causal_receipts WHERE kind='face-rejection' AND field=CAST(OLD.id AS TEXT);
                DELETE FROM metadata_edit_conflicts WHERE kind='face-rejection' AND field=CAST(OLD.id AS TEXT);
            END;
            PRAGMA user_version=70;
            """;
        command.ExecuteNonQuery();
        foreach(var operation in new[]{"INSERT","UPDATE","DELETE"})
        {
            command.CommandText=$"""
                CREATE TRIGGER IF NOT EXISTS space_sync_rejection_adoption_{operation} AFTER {operation} ON face_person_rejections
                BEGIN UPDATE sync_identity_offer_revision SET revision=revision+1 WHERE id=1; END;
                """;
            command.ExecuteNonQuery();
        }
        foreach(var operation in new[]{"INSERT","UPDATE"})
        {
            var changed=operation=="UPDATE"?" AND (NEW.value IS NOT OLD.value OR NEW.operation_id IS NOT OLD.operation_id)":"";
            command.CommandText=$"""
                CREATE TRIGGER IF NOT EXISTS space_sync_face_field_notify_{operation} AFTER {operation} ON metadata_edits
                WHEN NEW.kind IN ('face-state','face-rejection') AND NOT EXISTS(SELECT 1 FROM sync_suppression WHERE id=1){changed}
                BEGIN
                    UPDATE sync_identity_offer_revision SET revision=revision+1 WHERE id=1;
                    INSERT INTO sync_publisher_notifications(publisher_id,revision)
                    SELECT DISTINCT publisher_id,1 FROM sync_entity_identities WHERE kind='face' AND target_id=NEW.entity_id
                    ON CONFLICT(publisher_id) DO UPDATE SET revision=revision+1;
                END;
                """;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
