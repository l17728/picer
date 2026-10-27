using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class CatalogRetirementMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction=connection.BeginTransaction();using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="""
            CREATE TABLE IF NOT EXISTS sync_catalog_retirements(
                operation_id TEXT PRIMARY KEY,publisher_id TEXT NOT NULL REFERENCES sync_publishers(publisher_id) ON DELETE CASCADE,
                kind TEXT NOT NULL CHECK(kind IN ('tag','album','person')),catalog_id INTEGER NOT NULL,global_id TEXT NOT NULL,
                state TEXT NOT NULL DEFAULT 'pending',created_utc TEXT NOT NULL,
                UNIQUE(publisher_id,kind,global_id));
            CREATE INDEX IF NOT EXISTS idx_catalog_retirement_pending ON sync_catalog_retirements(publisher_id,state);
            CREATE TABLE IF NOT EXISTS sync_catalog_retirement_media(
                operation_id TEXT NOT NULL REFERENCES sync_catalog_retirements(operation_id) ON DELETE CASCADE,
                media_kind TEXT NOT NULL CHECK(media_kind IN ('image','video')),media_id INTEGER NOT NULL,
                source_id INTEGER NOT NULL,media_global_id TEXT NOT NULL,
                PRIMARY KEY(operation_id,media_kind,media_global_id));
            CREATE INDEX IF NOT EXISTS idx_catalog_retirement_media ON sync_catalog_retirement_media(media_kind,media_id);
            CREATE TRIGGER IF NOT EXISTS catalog_retirement_purge_image AFTER DELETE ON images BEGIN
                DELETE FROM sync_catalog_retirement_media WHERE media_kind='image' AND media_id=OLD.id;
            END;
            CREATE TRIGGER IF NOT EXISTS catalog_retirement_purge_video AFTER DELETE ON videos BEGIN
                DELETE FROM sync_catalog_retirement_media WHERE media_kind='video' AND media_id=OLD.id;
            END;
            PRAGMA user_version=77;
            """;
        command.ExecuteNonQuery();transaction.Commit();
    }
}
