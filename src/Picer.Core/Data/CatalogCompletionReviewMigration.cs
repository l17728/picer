using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class CatalogCompletionReviewMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction=connection.BeginTransaction();using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="""
            CREATE TABLE IF NOT EXISTS sync_catalog_completion_reviews(
                publisher_id TEXT NOT NULL REFERENCES sync_publishers(publisher_id) ON DELETE CASCADE,
                operation_id TEXT NOT NULL,kind TEXT NOT NULL,global_id TEXT NOT NULL,
                media_kind TEXT NOT NULL,media_global_id TEXT NOT NULL,media_id INTEGER NOT NULL,catalog_id INTEGER NOT NULL,
                state TEXT NOT NULL DEFAULT 'pending',current_fingerprint TEXT NOT NULL,applied_operations TEXT NOT NULL DEFAULT '[]',created_utc TEXT NOT NULL,
                PRIMARY KEY(publisher_id,operation_id,media_kind,media_global_id));
            CREATE INDEX IF NOT EXISTS idx_catalog_completion_review_media ON sync_catalog_completion_reviews(media_kind,media_id);
            CREATE INDEX IF NOT EXISTS idx_catalog_completion_review_pending ON sync_catalog_completion_reviews(state,created_utc);
            CREATE TRIGGER IF NOT EXISTS catalog_completion_review_purge_image AFTER DELETE ON images BEGIN
                DELETE FROM sync_catalog_completion_reviews WHERE media_kind='image' AND media_id=OLD.id;
            END;
            CREATE TRIGGER IF NOT EXISTS catalog_completion_review_purge_video AFTER DELETE ON videos BEGIN
                DELETE FROM sync_catalog_completion_reviews WHERE media_kind='video' AND media_id=OLD.id;
            END;
            PRAGMA user_version=86;
            """;
        command.ExecuteNonQuery();transaction.Commit();
    }
}
