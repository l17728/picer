using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class MetadataCausalMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        var columns = new HashSet<string>(StringComparer.Ordinal);
        command.CommandText = "PRAGMA table_info(metadata_edits)";
        using (var rows = command.ExecuteReader()) while (rows.Read()) columns.Add(rows.GetString(1));
        foreach (var column in new[] { "server_version", "base_version" })
        {
            if (columns.Contains(column)) continue;
            command.CommandText = $"ALTER TABLE metadata_edits ADD COLUMN {column} INTEGER NOT NULL DEFAULT 0";
            command.ExecuteNonQuery();
        }
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS metadata_version_clock(id INTEGER PRIMARY KEY CHECK(id=1),version INTEGER NOT NULL);
            INSERT OR IGNORE INTO metadata_version_clock VALUES(1,0);
            CREATE INDEX IF NOT EXISTS ix_metadata_edits_server_version ON metadata_edits(server_version);
            CREATE INDEX IF NOT EXISTS ix_metadata_edits_content_field ON metadata_edits(kind,field,video_content_identity);
            CREATE TABLE IF NOT EXISTS metadata_causal_receipts(
                operation_id TEXT PRIMARY KEY,kind TEXT NOT NULL,entity_id INTEGER NOT NULL,field TEXT NOT NULL,
                request_json TEXT NOT NULL,result_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS metadata_edit_conflicts(
                operation_id TEXT PRIMARY KEY,kind TEXT NOT NULL,entity_id INTEGER NOT NULL,field TEXT NOT NULL,
                proposed_json TEXT NOT NULL,current_json TEXT NOT NULL,state TEXT NOT NULL DEFAULT 'pending');
            CREATE INDEX IF NOT EXISTS ix_metadata_conflicts_state ON metadata_edit_conflicts(state);
            CREATE INDEX IF NOT EXISTS ix_metadata_conflicts_entity ON metadata_edit_conflicts(kind,entity_id,field);
            CREATE INDEX IF NOT EXISTS ix_metadata_conflicts_field ON metadata_edit_conflicts(kind,field);
            CREATE INDEX IF NOT EXISTS ix_metadata_receipts_entity ON metadata_causal_receipts(kind,entity_id,field);
            CREATE INDEX IF NOT EXISTS ix_metadata_receipts_field ON metadata_causal_receipts(kind,field);
            """;
        command.ExecuteNonQuery();
        foreach (var (table, predicate) in new[] {
            ("images", "(kind='tag-link' AND entity_id=OLD.id) OR (kind='album-link' AND field=CAST(OLD.id AS TEXT))"),
            ("people", "kind='person' AND entity_id=OLD.id"),
            ("albums", "kind='album-link' AND entity_id=OLD.id"),
            ("tags", "kind IN ('tag-link','video-tag-link') AND field=CAST(OLD.id AS TEXT)"),
            ("videos", "kind IN ('video-tag-link','video-concept') AND entity_id=OLD.id") })
        {
            command.CommandText = $"""
                CREATE TRIGGER IF NOT EXISTS space_sync_causal_purge_{table} AFTER DELETE ON {table}
                BEGIN
                    DELETE FROM metadata_causal_receipts WHERE {predicate};
                    DELETE FROM metadata_edit_conflicts WHERE {predicate};
                END;
                """;
            command.ExecuteNonQuery();
        }
        command.CommandText = "PRAGMA user_version=62"; command.ExecuteNonQuery(); transaction.Commit();
    }
}
