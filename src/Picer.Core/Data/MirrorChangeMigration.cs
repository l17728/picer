using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class MirrorChangeMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE mirror_revision(id INTEGER PRIMARY KEY CHECK(id=1),epoch TEXT NOT NULL,revision INTEGER NOT NULL,metadata_revision INTEGER NOT NULL);
            INSERT INTO mirror_revision VALUES(1,lower(hex(randomblob(16))),0,1);
            CREATE TABLE mirror_media_changes(kind TEXT NOT NULL,media_id INTEGER NOT NULL,source_id INTEGER NOT NULL,revision INTEGER NOT NULL,deleted INTEGER NOT NULL,PRIMARY KEY(kind,media_id));
            CREATE UNIQUE INDEX idx_mirror_changes_revision ON mirror_media_changes(revision);
            CREATE TABLE mirror_checkpoint(id INTEGER PRIMARY KEY CHECK(id=1),epoch TEXT NOT NULL,media_revision INTEGER NOT NULL,metadata_revision INTEGER NOT NULL);
            CREATE TABLE mirror_owned_media(kind TEXT NOT NULL,media_id INTEGER NOT NULL,seen_epoch TEXT NOT NULL,PRIMARY KEY(kind,media_id));
            """;
        command.ExecuteNonQuery();
        foreach (var (table, kind) in new[] { ("images", "image"), ("videos", "video") })
        {
            foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                var row = operation == "DELETE" ? "OLD" : "NEW";
                command.CommandText = $"""
                    CREATE TRIGGER mirror_change_{table}_{operation.ToLowerInvariant()} AFTER {operation} ON {table}
                    BEGIN
                        UPDATE mirror_revision SET revision=revision+1 WHERE id=1;
                        INSERT INTO mirror_media_changes(kind,media_id,source_id,revision,deleted)
                        VALUES('{kind}',{row}.id,{row}.source_id,(SELECT revision FROM mirror_revision WHERE id=1),{(operation == "DELETE" ? 1 : 0)})
                        ON CONFLICT(kind,media_id) DO UPDATE SET source_id=excluded.source_id,revision=excluded.revision,deleted=excluded.deleted;
                    END;
                    """;
                command.ExecuteNonQuery();
            }
            // Seed existing rows through the same ordered protocol, without rewriting media.
            command.CommandText = $"""
                INSERT INTO mirror_media_changes(kind,media_id,source_id,revision,deleted)
                SELECT '{kind}',id,source_id,(SELECT revision FROM mirror_revision WHERE id=1)+ROW_NUMBER() OVER(ORDER BY id),0 FROM {table};
                UPDATE mirror_revision SET revision=COALESCE((SELECT MAX(revision) FROM mirror_media_changes),0) WHERE id=1;
                """;
            command.ExecuteNonQuery();
        }
        // Metadata has its own checkpoint: media paging never acknowledges a failed metadata refresh.
        var tables = new List<string>();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        using (var reader = command.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));
        foreach (var table in tables.Where(IsMetadataTable))
        foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            var quoted = table.Replace("\"", "\"\"", StringComparison.Ordinal);
            command.CommandText = $"CREATE TRIGGER \"mirror_metadata_{quoted}_{operation}\" AFTER {operation} ON \"{quoted}\" BEGIN UPDATE mirror_revision SET metadata_revision=metadata_revision+1 WHERE id=1; END;";
            command.ExecuteNonQuery();
        }
    }

    private static bool IsMetadataTable(string table) => table is "sources" or "tags" or "albums" or "album_images" or "image_tags" or "people" or "detected_faces" or "face_person_assignments" or "image_locations" or "cloud_vision_analyses" or "scene_group_descriptions"
        || table.StartsWith("face_", StringComparison.Ordinal)
        || table.StartsWith("video_", StringComparison.Ordinal);
}
