using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class CatalogGenerationMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction=connection.BeginTransaction();
        using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="""
            CREATE TABLE IF NOT EXISTS sync_catalog_generations(
                publisher_id TEXT NOT NULL REFERENCES sync_publishers(publisher_id) ON DELETE CASCADE,
                kind TEXT NOT NULL CHECK(kind IN ('tag','album','person')),global_id TEXT NOT NULL,previous_global_id TEXT NOT NULL,
                PRIMARY KEY(publisher_id,kind,global_id));
            PRAGMA user_version=82;
            """;
        command.ExecuteNonQuery();transaction.Commit();
    }
}
