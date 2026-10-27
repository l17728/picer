using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class SceneDescriptionSyncMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction=connection.BeginTransaction();using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="""
            CREATE TABLE IF NOT EXISTS sync_scene_bindings(
                publisher_id TEXT NOT NULL REFERENCES sync_publishers(publisher_id) ON DELETE CASCADE,
                local_fingerprint TEXT NOT NULL,target_fingerprint TEXT NOT NULL,source_confirmed INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(publisher_id,local_fingerprint));
            CREATE INDEX IF NOT EXISTS ix_scene_binding_target ON sync_scene_bindings(publisher_id,target_fingerprint,source_confirmed);
            CREATE TABLE IF NOT EXISTS scene_group_members(
                scene_fingerprint TEXT NOT NULL REFERENCES scene_group_descriptions(fingerprint) ON DELETE CASCADE,
                image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,PRIMARY KEY(scene_fingerprint,image_id));
            CREATE INDEX IF NOT EXISTS ix_scene_group_member_image ON scene_group_members(image_id,scene_fingerprint);
            INSERT OR IGNORE INTO scene_group_members SELECT s.fingerprint,i.id FROM scene_group_descriptions s
                JOIN json_each(s.image_ids_json) member JOIN images i ON i.id=member.value;
            CREATE TRIGGER IF NOT EXISTS scene_group_member_insert AFTER INSERT ON scene_group_descriptions BEGIN
                INSERT OR IGNORE INTO scene_group_members SELECT NEW.fingerprint,i.id FROM json_each(NEW.image_ids_json) member JOIN images i ON i.id=member.value;
            END;
            CREATE TRIGGER IF NOT EXISTS scene_group_member_update AFTER UPDATE OF image_ids_json ON scene_group_descriptions BEGIN
                DELETE FROM scene_group_members WHERE scene_fingerprint=NEW.fingerprint;
                INSERT OR IGNORE INTO scene_group_members SELECT NEW.fingerprint,i.id FROM json_each(NEW.image_ids_json) member JOIN images i ON i.id=member.value;
            END;
            CREATE TRIGGER IF NOT EXISTS space_sync_scene_purge AFTER DELETE ON scene_group_descriptions BEGIN
                DELETE FROM sync_imported_metadata_edits WHERE operation_id IN(SELECT operation_id FROM metadata_edits WHERE kind='scene-description' AND field=OLD.fingerprint);
                DELETE FROM metadata_edits WHERE kind='scene-description' AND field=OLD.fingerprint;
                DELETE FROM metadata_edit_conflicts WHERE kind='scene-description' AND field=OLD.fingerprint;
                DELETE FROM metadata_causal_receipts WHERE kind='scene-description' AND field=OLD.fingerprint;
                DELETE FROM sync_scene_bindings WHERE local_fingerprint=OLD.fingerprint OR target_fingerprint=OLD.fingerprint;
            END;
            """;command.ExecuteNonQuery();
        foreach(var operation in new[]{"INSERT","UPDATE","DELETE"})
        {
            var row=operation=="DELETE"?"OLD":"NEW";
            command.CommandText=$"""
                CREATE TRIGGER IF NOT EXISTS space_sync_scene_group_{operation} AFTER {operation} ON scene_group_descriptions
                WHEN NOT EXISTS(SELECT 1 FROM sync_suppression WHERE id=1)
                BEGIN
                    INSERT INTO sync_metadata_changes(publisher_id,global_image_id,changed_utc,media_kind)
                    SELECT DISTINCT e.publisher_id,e.global_id,strftime('%Y-%m-%dT%H:%M:%fZ','now'),'image'
                    FROM json_each({row}.image_ids_json) member JOIN sync_entity_identities e ON e.kind='image' AND e.target_id=member.value
                    WHERE NOT EXISTS(SELECT 1 FROM json_each({row}.image_ids_json) other
                        WHERE NOT EXISTS(SELECT 1 FROM sync_entity_identities owned WHERE owned.publisher_id=e.publisher_id AND owned.kind='image' AND owned.target_id=other.value));
                END;
                """;command.ExecuteNonQuery();
        }
        command.CommandText="""
            INSERT OR IGNORE INTO metadata_edits(kind,entity_id,field,value,updated_utc,operation_id,server_version,base_version)
            SELECT 'scene-description',(SELECT MIN(CAST(value AS INTEGER)) FROM json_each(image_ids_json)),fingerprint,
                json_object('SceneName',scene_name,'Summary',summary,'SearchTerms',json(search_terms_json),
                    'Provider',provider,'ModelVersion',model_version,'PromptVersion',prompt_version),
                strftime('%Y-%m-%dT%H:%M:%f','now')||'0000Z',lower(hex(randomblob(16))),0,0
            FROM scene_group_descriptions s WHERE state='accepted' AND provider<>'picer-local-layout'
                AND json_valid(image_ids_json) AND json_array_length(image_ids_json)>0 AND json_valid(search_terms_json)
                AND length(trim(scene_name)) BETWEEN 1 AND 200 AND length(summary)<=8000
                AND length(provider) BETWEEN 1 AND 256 AND length(model_version)<=256 AND length(prompt_version)<=256
                AND json_array_length(search_terms_json)<=20
                AND NOT EXISTS(SELECT 1 FROM json_each(search_terms_json) WHERE type<>'text' OR length(trim(value))=0 OR length(value)>80)
                AND NOT EXISTS(SELECT 1 FROM json_each(image_ids_json) member WHERE NOT EXISTS(SELECT 1 FROM images WHERE id=member.value));
            PRAGMA user_version=89;
            """;command.ExecuteNonQuery();transaction.Commit();
    }
}
