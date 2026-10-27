using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class VideoFaceManualMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction=connection.BeginTransaction();
        using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="""
            INSERT OR IGNORE INTO metadata_edits(kind,entity_id,field,value,updated_utc,operation_id)
            SELECT 'video-face-state',f.id,'state',
                json_object('PersonId',CASE WHEN f.assignment_state='confirmed' THEN f.assigned_person_id ELSE NULL END,
                    'State',f.assignment_state,'ModelVersion',f.model_version),
                strftime('%Y-%m-%dT%H:%M:%f','now')||'0000Z',lower(hex(randomblob(16)))
            FROM video_detected_faces f WHERE f.assignment_state IN ('confirmed','ignored')
                AND (f.assignment_state='ignored' OR f.assigned_person_id IS NOT NULL)
                AND EXISTS(SELECT 1 FROM video_effective_faces owned WHERE owned.id=f.id);
            INSERT OR IGNORE INTO metadata_edits(kind,entity_id,field,value,updated_utc,operation_id)
            SELECT 'video-face-rejection',r.video_face_id,CAST(r.person_id AS TEXT),
                json_object('Rejected',json('true'),'ModelVersion',f.model_version),
                strftime('%Y-%m-%dT%H:%M:%f','now')||'0000Z',lower(hex(randomblob(16)))
            FROM video_face_person_rejections r JOIN video_detected_faces f ON f.id=r.video_face_id
            WHERE EXISTS(SELECT 1 FROM video_effective_faces owned WHERE owned.id=f.id);
            CREATE TRIGGER IF NOT EXISTS space_sync_video_face_field_purge AFTER DELETE ON video_detected_faces BEGIN
                DELETE FROM sync_imported_metadata_edits WHERE entity_kind IN ('video-face-state','video-face-rejection') AND entity_id=OLD.id;
                DELETE FROM metadata_edits WHERE kind IN ('video-face-state','video-face-rejection') AND entity_id=OLD.id;
                DELETE FROM metadata_causal_receipts WHERE kind IN ('video-face-state','video-face-rejection') AND entity_id=OLD.id;
                DELETE FROM metadata_edit_conflicts WHERE kind IN ('video-face-state','video-face-rejection') AND entity_id=OLD.id;
            END;
            CREATE TRIGGER IF NOT EXISTS space_sync_video_person_rejection_purge BEFORE DELETE ON people BEGIN
                DELETE FROM sync_imported_metadata_edits WHERE entity_kind='video-face-rejection' AND entity_field=CAST(OLD.id AS TEXT);
                DELETE FROM metadata_edits WHERE kind='video-face-rejection' AND field=CAST(OLD.id AS TEXT);
                DELETE FROM metadata_causal_receipts WHERE kind='video-face-rejection' AND field=CAST(OLD.id AS TEXT);
                DELETE FROM metadata_edit_conflicts WHERE kind='video-face-rejection' AND field=CAST(OLD.id AS TEXT);
            END;
            PRAGMA user_version=72;
            """;
        command.ExecuteNonQuery();
        foreach(var operation in new[]{"INSERT","UPDATE"})
        {
            var changed=operation=="UPDATE"?" AND (NEW.value IS NOT OLD.value OR NEW.operation_id IS NOT OLD.operation_id)":"";
            command.CommandText=$"""
                CREATE TRIGGER IF NOT EXISTS space_sync_video_face_field_notify_{operation} AFTER {operation} ON metadata_edits
                WHEN NEW.kind IN ('video-face-state','video-face-rejection') AND NOT EXISTS(SELECT 1 FROM sync_suppression WHERE id=1){changed}
                BEGIN
                    UPDATE mirror_revision SET metadata_revision=metadata_revision+1 WHERE id=1;
                    UPDATE sync_identity_offer_revision SET revision=revision+1 WHERE id=1;
                    INSERT INTO sync_publisher_notifications(publisher_id,revision)
                    SELECT DISTINCT publisher_id,1 FROM sync_entity_identities WHERE kind='video-face' AND target_id=NEW.entity_id
                    ON CONFLICT(publisher_id) DO UPDATE SET revision=revision+1;
                END;
                """;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
