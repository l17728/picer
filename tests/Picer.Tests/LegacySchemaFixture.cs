using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using System.Text.RegularExpressions;

namespace Picer.Tests;

internal static class LegacySchemaFixture
{
    internal static void RevertVersion43(SqliteConnection connection)
    {
        RevertVersion44(connection);
        var triggers = new List<(string Name, string Sql)>();
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT name,sql FROM sqlite_master WHERE type='trigger' AND name IN ('content_capture_detected_faces_insert','content_capture_detected_faces_update','content_restore_image','content_restore_image_update')";
            using var reader = query.ExecuteReader();
            while (reader.Read()) triggers.Add((reader.GetString(0), reader.GetString(1)));
        }
        foreach (var (name, sql) in triggers)
        {
            using var replace = connection.CreateCommand();
            replace.CommandText = "DROP TRIGGER " + name + ";" + sql
                .Replace("json_object('is_archived',r.is_archived,", "json_object(", StringComparison.Ordinal)
                .Replace("detected_faces(is_archived,", "detected_faces(", StringComparison.Ordinal)
                .Replace("SELECT COALESCE(json_extract(d.payload,'$.is_archived'),0),", "SELECT ", StringComparison.Ordinal);
            replace.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TABLE analysis_billing_events;
            DROP TABLE analysis_budget_reservations;
            DROP INDEX idx_images_source_path_nocase;
            DROP INDEX idx_videos_source_path_nocase;
            DROP INDEX idx_images_directory_path;
            DROP INDEX idx_videos_directory_path;
            ALTER TABLE detected_faces DROP COLUMN is_archived;
            UPDATE content_derivatives SET payload=json_remove(payload,'$.is_archived') WHERE kind='detected_faces';
            PRAGMA user_version=42;
            """;
        command.ExecuteNonQuery();
    }

    internal static void RevertVersion45(SqliteConnection connection)
    {
        using (var reset = connection.CreateCommand())
        {
            reset.CommandText = """
                DROP TABLE IF EXISTS sync_source_bindings;
                DROP TABLE IF EXISTS sync_exports;
                DROP TABLE IF EXISTS sync_outbox;
                DROP TABLE IF EXISTS sync_operation_receipts;
                DROP TABLE IF EXISTS sync_entity_identities;
                DROP TABLE IF EXISTS sync_publishers;
                DROP TABLE IF EXISTS sync_purge_journal;
                DROP TABLE IF EXISTS sync_space_state;
                DROP TABLE IF EXISTS library_identity;
                """;
            reset.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM pragma_table_info('metadata_edits') WHERE name='video_content_identity'";
        if (command.ExecuteScalar() is null) return;
        command.CommandText = "DROP TRIGGER IF EXISTS video_metadata_bind_verified_content; DROP TABLE IF EXISTS video_metadata_operation_bindings; ALTER TABLE metadata_edits DROP COLUMN video_content_identity; PRAGMA user_version=44;";
        command.ExecuteNonQuery();
    }

    internal static void RevertVersion44(SqliteConnection connection)
    {
        RevertVersion45(connection);
        using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='video_contents'";
        if (probe.ExecuteScalar() is null) return;
        Execute("PRAGMA foreign_keys=OFF");
        try
        {
            using var transaction = connection.BeginTransaction();
            probe.Transaction = transaction;
            var triggers = new List<string>();
            probe.CommandText = "SELECT name FROM sqlite_master WHERE type='trigger' AND (name LIKE 'mirror_%' OR name LIKE 'video_content_%' OR name LIKE 'description_text_video%')";
            using (var reader = probe.ExecuteReader()) while (reader.Read()) triggers.Add(reader.GetString(0));
            foreach (var name in triggers) Execute("DROP TRIGGER \"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
            Execute("DROP TABLE IF EXISTS mirror_media_changes; DROP TABLE IF EXISTS mirror_revision; DROP TABLE IF EXISTS mirror_checkpoint; DROP TABLE IF EXISTS mirror_owned_media;");
            var indexes = new List<string>();
            foreach (var table in VideoContentMigration.Tables)
            {
                probe.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND tbl_name=$table AND sql IS NOT NULL";
                probe.Parameters.Clear(); probe.Parameters.AddWithValue("$table", table);
                using (var reader = probe.ExecuteReader()) while (reader.Read()) indexes.Add(reader.GetString(0));
                probe.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$table";
                var sql = (string)probe.ExecuteScalar()!;
                sql = Regex.Replace(sql, "^CREATE TABLE\\s+(?:\"" + table + "\"|" + table + ")", "CREATE TABLE " + table + "_legacy", RegexOptions.IgnoreCase);
                sql = Regex.Replace(sql, "REFERENCES\\s+video_contents\\s*\\(id\\)", "REFERENCES videos(id)", RegexOptions.IgnoreCase);
                Execute(sql);
                var columns = new List<string>();
                probe.CommandText = "PRAGMA table_info(" + table + ")"; probe.Parameters.Clear();
                using (var reader = probe.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(1));
                var values = columns.Select(column => column == "video_id"
                    ? "(SELECT MIN(id) FROM videos WHERE content_id=r.video_id)" : "r." + column);
                Execute($"INSERT INTO {table}_legacy({string.Join(',', columns)}) SELECT {string.Join(',', values)} FROM {table} r");
            }
            foreach (var table in VideoContentMigration.Tables.Reverse()) Execute("DROP TABLE " + table);
            foreach (var table in VideoContentMigration.Tables) Execute($"ALTER TABLE {table}_legacy RENAME TO {table}");
            foreach (var sql in indexes) Execute(sql);
            Execute("DROP TABLE video_content_decision_history; DROP INDEX idx_videos_content; ALTER TABLE videos DROP COLUMN content_id; DROP TABLE video_contents; PRAGMA user_version=43;");
            transaction.Commit();
        }
        finally { Execute("PRAGMA foreign_keys=ON"); }

        void Execute(string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    }
}
