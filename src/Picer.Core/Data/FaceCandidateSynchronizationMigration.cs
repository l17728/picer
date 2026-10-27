using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class FaceCandidateSynchronizationMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction=connection.BeginTransaction();using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="""
            CREATE TRIGGER IF NOT EXISTS space_sync_row_face_candidate_members_DELETE AFTER DELETE ON face_candidate_members
            WHEN NOT EXISTS(SELECT 1 FROM sync_suppression WHERE id=1) AND EXISTS(SELECT 1 FROM sync_publishers)
                AND EXISTS(SELECT 1 FROM detected_faces WHERE id=OLD.face_id)
            BEGIN
                INSERT INTO sync_metadata_row_journal(operation_id,table_name,row_key,operation,before_payload)
                VALUES(lower(hex(randomblob(16))),'face_candidate_members',json_array(OLD.cluster_id,OLD.face_id),'delete',
                    json_object('cluster_id',OLD.cluster_id,'face_id',OLD.face_id,'similarity',OLD.similarity,
                        'image_id',(SELECT image_id FROM detected_faces WHERE id=OLD.face_id),
                        'model_version',(SELECT model_version FROM detected_faces WHERE id=OLD.face_id)));
            END;
            """;
        command.ExecuteNonQuery();
        command.CommandText="SELECT sql FROM sqlite_master WHERE type='trigger' AND name='space_sync_row_purge_image'";
        var purge=(string)command.ExecuteScalar()!;
        command.CommandText="DROP TRIGGER space_sync_row_purge_image";command.ExecuteNonQuery();
        command.CommandText=purge.Replace("'face_corrections')","'face_corrections','face_candidate_members')",StringComparison.Ordinal);command.ExecuteNonQuery();
        foreach(var operation in new[]{"INSERT","UPDATE","DELETE"})
        {
            var row=operation=="DELETE"?"OLD":"NEW";
            command.CommandText=$"""
                CREATE TRIGGER IF NOT EXISTS space_sync_face_candidate_members_{operation} AFTER {operation} ON face_candidate_members
                WHEN NOT EXISTS(SELECT 1 FROM sync_suppression WHERE id=1)
                BEGIN
                    INSERT INTO sync_metadata_changes(publisher_id,global_image_id,changed_utc,media_kind)
                    SELECT e.publisher_id,e.global_id,strftime('%Y-%m-%dT%H:%M:%fZ','now'),'image'
                    FROM detected_faces f JOIN sync_entity_identities e ON e.kind='image' AND e.target_id=f.image_id
                    WHERE f.id={row}.face_id;
                END;
                """;command.ExecuteNonQuery();
        }
        command.CommandText="PRAGMA user_version=88";command.ExecuteNonQuery();transaction.Commit();
    }
}
