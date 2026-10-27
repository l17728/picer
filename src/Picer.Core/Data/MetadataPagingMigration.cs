using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class MetadataPagingMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction=connection.BeginTransaction();
        using var command=connection.CreateCommand(); command.Transaction=transaction;
        foreach (var (column,definition) in new[]{("downloaded_metadata_version","INTEGER NOT NULL DEFAULT 0"),
                     ("downloaded_metadata_operation","TEXT NOT NULL DEFAULT ''"),("downloaded_scope_hash","TEXT NOT NULL DEFAULT ''")})
        {
            command.CommandText="SELECT 1 FROM pragma_table_info('sync_publishers') WHERE name=$column";
            command.Parameters.AddWithValue("$column",column); var exists=command.ExecuteScalar() is not null; command.Parameters.Clear();
            if (exists) continue;
            command.CommandText=$"ALTER TABLE sync_publishers ADD COLUMN {column} {definition}"; command.ExecuteNonQuery();
        }
        command.CommandText="CREATE INDEX IF NOT EXISTS ix_metadata_edits_version_operation ON metadata_edits(server_version,operation_id); PRAGMA user_version=66;";
        command.ExecuteNonQuery(); transaction.Commit();
    }
}
