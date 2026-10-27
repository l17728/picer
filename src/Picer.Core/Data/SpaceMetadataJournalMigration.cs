using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

/// <summary>Captures committed row changes before a deleted row loses its identity and payload.
/// This device-local journal is not a wire identity or an acknowledgement of delivery.</summary>
internal static class SpaceMetadataJournalMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sync_metadata_row_journal(
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                operation_id TEXT NOT NULL UNIQUE,
                table_name TEXT NOT NULL,
                row_key TEXT NOT NULL,
                operation TEXT NOT NULL CHECK(operation IN ('upsert','delete')),
                before_payload TEXT,
                after_payload TEXT,
                created_utc TEXT NOT NULL DEFAULT(strftime('%Y-%m-%dT%H:%M:%fZ','now'))
            );
            CREATE INDEX IF NOT EXISTS ix_sync_metadata_row_journal_table
                ON sync_metadata_row_journal(table_name);
            """;
        command.ExecuteNonQuery();
        // The list is deliberately explicit: embeddings, credentials, caches and original
        // media are not copied into this journal. Authorization and global identity binding
        // are required separately before a journal entry may leave the device.
        foreach (var table in new[] { "tags", "albums", "people", "person_aliases", "image_tags", "album_images",
            "image_locations", "cloud_vision_analyses", "image_classification_assertions",
            "image_classification_rejections", "semantic_content_clusters", "semantic_content_cluster_members",
            "detected_faces", "face_person_assignments", "face_person_rejections", "face_review_states",
            "face_corrections", "video_keyframes", "video_detected_faces", "video_summaries", "video_transcripts" })
        {
            var columns = new List<(string Name, int Key)>();
            command.CommandText = $"PRAGMA table_info(\"{table}\")";
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    var name = reader.GetString(1);
                    if (reader.GetString(2).Contains("BLOB", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("embedding", StringComparison.OrdinalIgnoreCase)) continue;
                    columns.Add((name, reader.GetInt32(5)));
                }
            if (columns.Count == 0) continue; // Older supported fixtures may lack optional tables.
            var keys = columns.Where(column => column.Key > 0).OrderBy(column => column.Key).ToArray();
            if (table == "image_classification_rejections")
                keys = new[] { "image_id", "content_sha256", "concept_id", "source_scope" }
                    .Select(name => (Name: name, Key: 0)).ToArray();
            if (keys.Length == 0) throw new InvalidDataException("metadata_journal_primary_key_missing");
            string Payload(string row) => "json_object(" + string.Join(",", columns.Select(column =>
                $"'{column.Name}',{row}.\"{column.Name}\"")) + ")";
            string Key(string row) => "json_array(" + string.Join(",", keys.Select(column =>
                $"{row}.\"{column.Name}\"")) + ")";
            foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                var before = operation == "INSERT" ? "NULL" : Payload("OLD");
                var after = operation == "DELETE" ? "NULL" : Payload("NEW");
                var row = operation == "DELETE" ? "OLD" : "NEW";
                // No-op updates should not grow the outbox. Remote application is guarded
                // by the same transaction-local suppression used by the publisher.
                var changed = operation == "UPDATE" ? $" AND {before} IS NOT {after}" : "";
                command.CommandText = $"""
                    CREATE TRIGGER IF NOT EXISTS space_sync_row_{table}_{operation}
                    AFTER {operation} ON "{table}"
                    WHEN NOT EXISTS(SELECT 1 FROM sync_suppression WHERE id=1)
                    AND EXISTS(SELECT 1 FROM sync_publishers){changed}
                    BEGIN
                        INSERT INTO sync_metadata_row_journal(operation_id,table_name,row_key,operation,before_payload,after_payload)
                        VALUES(lower(hex(randomblob(16))),'{table}',{Key(row)},'{(operation == "DELETE" ? "delete" : "upsert")}',{before},{after});
                    END;
                    """;
                command.ExecuteNonQuery();
            }
        }
        // A permanent index purge must not leave media metadata in the new journal.
        // These parent AFTER DELETE triggers run after dependent FK cascades, so
        // they also remove deletion entries produced by the child triggers.
        command.CommandText = """
            CREATE TRIGGER IF NOT EXISTS space_sync_row_purge_image AFTER DELETE ON images
            BEGIN
                DELETE FROM sync_metadata_row_journal
                WHERE table_name IN ('face_person_assignments','face_person_rejections','face_review_states','face_corrections')
                AND COALESCE(json_extract(before_payload,'$.face_id'),json_extract(after_payload,'$.face_id')) IN (
                    SELECT COALESCE(json_extract(before_payload,'$.id'),json_extract(after_payload,'$.id'))
                    FROM sync_metadata_row_journal WHERE table_name='detected_faces'
                    AND (json_extract(before_payload,'$.image_id')=OLD.id OR json_extract(after_payload,'$.image_id')=OLD.id));
                DELETE FROM sync_metadata_row_journal
                WHERE table_name IN ('image_tags','album_images','image_locations','cloud_vision_analyses',
                    'image_classification_assertions','image_classification_rejections',
                    'semantic_content_cluster_members','detected_faces')
                AND (json_extract(before_payload,'$.image_id')=OLD.id OR json_extract(after_payload,'$.image_id')=OLD.id);
                DELETE FROM sync_metadata_row_journal
                WHERE table_name='image_classification_rejections'
                AND lower(COALESCE(json_extract(before_payload,'$.content_sha256'),json_extract(after_payload,'$.content_sha256')))=lower(COALESCE(OLD.content_sha256,OLD.sha256))
                AND NOT EXISTS(SELECT 1 FROM images WHERE lower(COALESCE(content_sha256,sha256))=lower(COALESCE(OLD.content_sha256,OLD.sha256)));
            END;
            CREATE TRIGGER IF NOT EXISTS space_sync_row_purge_video_content AFTER DELETE ON video_contents
            BEGIN
                DELETE FROM sync_metadata_row_journal
                WHERE table_name IN ('video_keyframes','video_detected_faces','video_summaries','video_transcripts')
                AND (json_extract(before_payload,'$.video_id')=OLD.id OR json_extract(after_payload,'$.video_id')=OLD.id);
            END;
            PRAGMA user_version=58;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
