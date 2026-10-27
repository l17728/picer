using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class CatalogRetirementOrderingMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction=connection.BeginTransaction();
        using var command=connection.CreateCommand();command.Transaction=transaction;
        foreach(var (table,column,definition) in new[]
        {
            ("sync_catalog_retirements","sealed_metadata_revision","INTEGER"),
            ("sync_catalog_retirements","captured_media_count","INTEGER"),
            ("sync_catalog_retirement_receipts","applied_operations","TEXT NOT NULL DEFAULT '[]'")
        })
        {
            command.CommandText=$"SELECT 1 FROM pragma_table_info('{table}') WHERE name=$column";
            command.Parameters.AddWithValue("$column",column);
            var exists=command.ExecuteScalar() is not null;command.Parameters.Clear();
            if(exists) continue;
            command.CommandText=$"ALTER TABLE {table} ADD COLUMN {column} {definition}";
            command.ExecuteNonQuery();
        }
        // Historical rows have no proven publication cut. Do not invent one.
        command.CommandText="PRAGMA user_version=80";command.ExecuteNonQuery();transaction.Commit();
    }
}
