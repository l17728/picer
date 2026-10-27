using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class CatalogRetirementBaselineMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction=connection.BeginTransaction();
        using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="""
            CREATE TABLE IF NOT EXISTS sync_catalog_retirement_baselines(
                operation_id TEXT NOT NULL,media_kind TEXT NOT NULL,media_global_id TEXT NOT NULL,
                fingerprint TEXT NOT NULL,
                PRIMARY KEY(operation_id,media_kind,media_global_id),
                FOREIGN KEY(operation_id,media_kind,media_global_id)
                    REFERENCES sync_catalog_retirement_media(operation_id,media_kind,media_global_id) ON DELETE CASCADE);
            PRAGMA user_version=78;
            """;
        // Older retirement records have already lost their pre-delete fields.
        // Missing evidence must remain explicit; never invent a clean baseline.
        command.ExecuteNonQuery();transaction.Commit();
    }
}
