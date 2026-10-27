using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class SpaceIdentityAdoptionMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sync_identity_offers(
                offer_id TEXT PRIMARY KEY,
                publisher_id TEXT NOT NULL REFERENCES sync_publishers(publisher_id) ON DELETE CASCADE,
                kind TEXT NOT NULL,target_id INTEGER NOT NULL,
                UNIQUE(publisher_id,kind,target_id));
            CREATE TABLE IF NOT EXISTS sync_pending_identity_adoptions(
                publisher_id TEXT NOT NULL REFERENCES sync_publishers(publisher_id) ON DELETE CASCADE,
                offer_id TEXT NOT NULL,kind TEXT NOT NULL,local_id INTEGER NOT NULL,global_id TEXT NOT NULL,
                PRIMARY KEY(publisher_id,offer_id));
            CREATE TABLE IF NOT EXISTS sync_adopted_person_labels(
                publisher_id TEXT NOT NULL REFERENCES sync_publishers(publisher_id) ON DELETE CASCADE,
                local_id INTEGER NOT NULL REFERENCES people(id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED,
                remote_name TEXT NOT NULL,display_name TEXT NOT NULL,
                PRIMARY KEY(publisher_id,local_id));
            CREATE TABLE IF NOT EXISTS sync_adoption_scan_checkpoints(
                publisher_id TEXT NOT NULL REFERENCES sync_publishers(publisher_id) ON DELETE CASCADE,
                scope_hash TEXT NOT NULL,revision_key TEXT NOT NULL,
                PRIMARY KEY(publisher_id,scope_hash));
            CREATE TABLE IF NOT EXISTS sync_identity_offer_revision(id INTEGER PRIMARY KEY CHECK(id=1),revision INTEGER NOT NULL);
            INSERT OR IGNORE INTO sync_identity_offer_revision VALUES(1,1);
            """;
        command.ExecuteNonQuery();
        // Description/status updates are intentionally absent: new identity discovery
        // depends on relationships, not on every model result written to the library.
        foreach (var table in new[] { "sync_entity_identities", "sync_source_bindings", "image_tags", "album_images",
            "video_tags", "detected_faces", "video_keyframes", "video_detected_faces", "face_person_assignments", "images", "videos" })
        {
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$table";
            command.Parameters.AddWithValue("$table", table);
            var exists = command.ExecuteScalar() is not null;
            command.Parameters.Clear();
            if (!exists) continue;
            foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                var changed = operation == "UPDATE" ? table switch
                {
                    "images" => " AND NEW.source_id IS NOT OLD.source_id",
                    "videos" => " AND (NEW.source_id IS NOT OLD.source_id OR NEW.content_id IS NOT OLD.content_id)",
                    "video_detected_faces" => " AND (NEW.video_id IS NOT OLD.video_id OR NEW.assigned_person_id IS NOT OLD.assigned_person_id OR NEW.suggested_person_id IS NOT OLD.suggested_person_id)",
                    "detected_faces" => " AND NEW.image_id IS NOT OLD.image_id",
                    "video_keyframes" => " AND NEW.video_id IS NOT OLD.video_id",
                    "face_person_assignments" => " AND NEW.person_id IS NOT OLD.person_id",
                    "sync_source_bindings" => " AND (NEW.publisher_id IS NOT OLD.publisher_id OR NEW.local_source_id IS NOT OLD.local_source_id OR NEW.source_id IS NOT OLD.source_id)",
                    "sync_entity_identities" => " AND (NEW.publisher_id IS NOT OLD.publisher_id OR NEW.kind IS NOT OLD.kind OR NEW.local_key IS NOT OLD.local_key OR NEW.global_id IS NOT OLD.global_id OR NEW.target_id IS NOT OLD.target_id)",
                    "image_tags" => " AND (NEW.image_id IS NOT OLD.image_id OR NEW.tag_id IS NOT OLD.tag_id)",
                    "album_images" => " AND (NEW.album_id IS NOT OLD.album_id OR NEW.image_id IS NOT OLD.image_id)",
                    "video_tags" => " AND (NEW.video_id IS NOT OLD.video_id OR NEW.tag_id IS NOT OLD.tag_id)",
                    _ => ""
                } : "";
                command.CommandText = $"""
                    CREATE TRIGGER IF NOT EXISTS space_sync_adoption_revision_{table}_{operation}
                    AFTER {operation} ON "{table}"
                    WHEN EXISTS(SELECT 1 FROM sync_publishers WHERE state='receiving'){changed}
                    BEGIN UPDATE sync_identity_offer_revision SET revision=revision+1 WHERE id=1; END;
                    """;
                command.ExecuteNonQuery();
            }
        }
        command.CommandText = "PRAGMA user_version=59";
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
