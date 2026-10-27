using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class SpaceInspectionMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sync_inspection_jobs(
                job_id TEXT PRIMARY KEY,operation_id TEXT NOT NULL UNIQUE,requester_grant TEXT NOT NULL,
                state TEXT NOT NULL DEFAULT 'active',created_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sync_inspection_targets(
                target_id TEXT PRIMARY KEY,job_id TEXT NOT NULL REFERENCES sync_inspection_jobs(job_id) ON DELETE CASCADE,
                publisher_id TEXT NOT NULL,device_id TEXT NOT NULL,device_grant_id TEXT NOT NULL,
                state TEXT NOT NULL DEFAULT 'queued',updated_utc TEXT NOT NULL,error_code TEXT,
                UNIQUE(job_id,publisher_id));
            CREATE INDEX IF NOT EXISTS ix_sync_inspection_targets_publisher_state ON sync_inspection_targets(publisher_id,state,updated_utc);
            CREATE TABLE IF NOT EXISTS sync_source_presence(
                publisher_id TEXT PRIMARY KEY REFERENCES sync_publishers(publisher_id) ON DELETE CASCADE,last_seen_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sync_publisher_notifications(
                publisher_id TEXT PRIMARY KEY REFERENCES sync_publishers(publisher_id) ON DELETE CASCADE,revision INTEGER NOT NULL DEFAULT 0);
            CREATE TRIGGER IF NOT EXISTS space_sync_notify_media AFTER INSERT ON sync_metadata_changes
            BEGIN
                INSERT INTO sync_publisher_notifications(publisher_id,revision) VALUES(NEW.publisher_id,1)
                ON CONFLICT(publisher_id) DO UPDATE SET revision=revision+1;
            END;
            """;
        command.ExecuteNonQuery();
        foreach (var (table, kind, id) in new[] { ("tags","tag","id"),("albums","album","id"),("people","person","id"),
                     ("person_aliases","person","person_id"),("image_tags","image","image_id"),("album_images","image","image_id"),
                     ("video_tags","video","video_id"),("video_visual_concepts","video","video_id") })
            foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                var row = operation == "DELETE" ? "OLD" : "NEW";
                var owners = table.StartsWith("video_", StringComparison.Ordinal)
                    ? $"SELECT DISTINCT e.publisher_id,1 FROM sync_entity_identities e JOIN videos v ON e.kind='video' AND e.target_id=v.id WHERE v.content_id={row}.{id}"
                    : $"SELECT DISTINCT publisher_id,1 FROM sync_entity_identities WHERE kind='{kind}' AND target_id={row}.{id}";
                string[] fields=table switch
                {
                    "tags"=>new[]{"name"}, "albums"=>["name","description"], "people"=>["display_name","description"],
                    "person_aliases"=>["person_id","alias"], "image_tags"=>["image_id","tag_id"],
                    "album_images"=>["album_id","image_id"], "video_tags"=>["video_id","tag_id"],
                    _=>new[]{"video_id","concept_id","model_version","state"}
                };
                var changed=operation=="UPDATE" ? " AND json_array("+string.Join(',',fields.Select(field=>"OLD."+field))+") IS NOT json_array("+
                    string.Join(',',fields.Select(field=>"NEW."+field))+")" : "";
                command.CommandText = $"""
                    CREATE TRIGGER IF NOT EXISTS space_sync_notify_{table}_{operation} AFTER {operation} ON {table}
                    WHEN NOT EXISTS(SELECT 1 FROM sync_suppression WHERE id=1){changed}
                    BEGIN
                        INSERT INTO sync_publisher_notifications(publisher_id,revision) {owners}
                        ON CONFLICT(publisher_id) DO UPDATE SET revision=revision+1;
                    END;
                    """;
                command.ExecuteNonQuery();
            }
        command.CommandText = "PRAGMA user_version=61"; command.ExecuteNonQuery(); transaction.Commit();
    }
}
