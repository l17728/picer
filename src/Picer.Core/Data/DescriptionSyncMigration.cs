using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class DescriptionSyncMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS description_outbox(
                kind TEXT NOT NULL,media_id INTEGER NOT NULL,provider TEXT NOT NULL,
                operation_id TEXT NOT NULL PRIMARY KEY,version_id TEXT NOT NULL,epoch TEXT NOT NULL,
                payload TEXT NOT NULL,acknowledged INTEGER NOT NULL DEFAULT 0,UNIQUE(epoch,version_id));
            CREATE TABLE IF NOT EXISTS description_versions(
                version_id TEXT NOT NULL,kind TEXT NOT NULL,media_id INTEGER NOT NULL,epoch TEXT NOT NULL,payload TEXT NOT NULL,
                PRIMARY KEY(epoch,version_id));
            CREATE INDEX IF NOT EXISTS description_versions_media ON description_versions(kind,media_id);
            CREATE TRIGGER IF NOT EXISTS description_version_revision AFTER INSERT ON description_versions
                WHEN NOT EXISTS(SELECT 1 FROM sync_suppression WHERE id=1)
                BEGIN UPDATE mirror_revision SET metadata_revision=metadata_revision+1 WHERE id=1; END;
            CREATE TRIGGER IF NOT EXISTS description_image_removed AFTER DELETE ON images BEGIN
                DELETE FROM description_versions WHERE kind='image' AND media_id=OLD.id;
                DELETE FROM description_outbox WHERE kind='image' AND media_id=OLD.id;
            END;
            CREATE TRIGGER IF NOT EXISTS description_video_removed AFTER DELETE ON videos BEGIN
                DELETE FROM description_versions WHERE kind='video' AND media_id=OLD.id;
                DELETE FROM description_outbox WHERE kind='video' AND media_id=OLD.id;
            END;
            CREATE TABLE IF NOT EXISTS description_receipts(operation_id TEXT PRIMARY KEY,payload_hash TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS description_recovery_state(id INTEGER PRIMARY KEY CHECK(id=1));
            PRAGMA user_version=92;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
