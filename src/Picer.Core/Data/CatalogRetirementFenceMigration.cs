using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class CatalogRetirementFenceMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction=connection.BeginTransaction();
        using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="""
            CREATE TABLE IF NOT EXISTS sync_retired_catalog_identities(
                publisher_id TEXT NOT NULL REFERENCES sync_publishers(publisher_id) ON DELETE CASCADE,
                kind TEXT NOT NULL CHECK(kind IN ('tag','album','person')),global_id TEXT NOT NULL,
                operation_id TEXT NOT NULL,confirmed_media_count INTEGER NOT NULL CHECK(confirmed_media_count>=0),
                catalog_removed INTEGER NOT NULL,created_utc TEXT NOT NULL,
                PRIMARY KEY(publisher_id,kind,global_id));
            CREATE VIEW IF NOT EXISTS sync_active_entity_identities AS
                SELECT e.* FROM sync_entity_identities e WHERE NOT EXISTS(
                    SELECT 1 FROM sync_retired_catalog_identities r WHERE r.publisher_id=e.publisher_id
                        AND r.kind=e.kind AND r.global_id=e.global_id);
            PRAGMA user_version=81;
            """;
        command.ExecuteNonQuery();transaction.Commit();
    }
}
