using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class FaceManualStateMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction=connection.BeginTransaction();using var command=connection.CreateCommand();command.Transaction=transaction;
        foreach(var (column,type) in new[]{("entity_kind","TEXT"),("entity_id","INTEGER")})
        {
            command.CommandText="SELECT 1 FROM pragma_table_info('sync_imported_metadata_edits') WHERE name=$column";
            command.Parameters.AddWithValue("$column",column);var exists=command.ExecuteScalar() is not null;command.Parameters.Clear();
            if(exists) continue;
            command.CommandText=$"ALTER TABLE sync_imported_metadata_edits ADD COLUMN {column} {type}";command.ExecuteNonQuery();
        }
        command.CommandText="""
            INSERT OR IGNORE INTO metadata_edits(kind,entity_id,field,value,updated_utc,operation_id)
            SELECT 'face-state',f.id,'state',json_object(
                'PersonId',CASE WHEN r.state='ignored' THEN NULL WHEN a.state='confirmed' THEN a.person_id ELSE NULL END,
                'State',CASE WHEN r.state='ignored' THEN 'ignored' WHEN a.state='confirmed' THEN 'confirmed' ELSE 'unassigned' END,
                'ModelVersion',f.model_version),strftime('%Y-%m-%dT%H:%M:%f','now')||'0000Z',lower(hex(randomblob(16)))
            FROM detected_faces f LEFT JOIN face_person_assignments a ON a.face_id=f.id LEFT JOIN face_review_states r ON r.face_id=f.id
            WHERE a.state='confirmed' OR r.state='ignored' OR EXISTS(SELECT 1 FROM face_corrections c WHERE c.face_id=f.id
                AND c.action IN ('move_to_unassigned','not_this_person','review_active','sync_review_active'));
            CREATE TRIGGER IF NOT EXISTS space_sync_face_manual_purge AFTER DELETE ON detected_faces BEGIN
                DELETE FROM sync_imported_metadata_edits WHERE entity_kind='face-state' AND entity_id=OLD.id;
                DELETE FROM metadata_edits WHERE kind='face-state' AND entity_id=OLD.id;
                DELETE FROM metadata_causal_receipts WHERE kind='face-state' AND entity_id=OLD.id;
                DELETE FROM metadata_edit_conflicts WHERE kind='face-state' AND entity_id=OLD.id;
            END;
            PRAGMA user_version=69;
            """;
        command.ExecuteNonQuery();transaction.Commit();
    }
}
