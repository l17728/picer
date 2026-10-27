using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class VideoContentIdentityMappingMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        foreach (var table in new[] { "sync_publishers", "sync_exports" })
        {
            command.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name='metadata_protocol'";
            if (command.ExecuteScalar() is not null) continue;
            command.CommandText = $"ALTER TABLE {table} ADD COLUMN metadata_protocol INTEGER NOT NULL DEFAULT 0";
            command.ExecuteNonQuery();
        }
        command.CommandText = """
            CREATE TABLE sync_video_content_identities(
                publisher_id TEXT NOT NULL REFERENCES sync_publishers(publisher_id) ON DELETE CASCADE,
                source_identity TEXT NOT NULL,
                content_id INTEGER NOT NULL REFERENCES video_contents(id) ON DELETE CASCADE,
                PRIMARY KEY(publisher_id,source_identity));
            CREATE INDEX ix_sync_video_content_target ON sync_video_content_identities(publisher_id,content_id);
            PRAGMA user_version=67;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
