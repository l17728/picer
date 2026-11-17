using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class DescriptionDeltaMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS description_changes(
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,epoch TEXT NOT NULL,version_id TEXT NOT NULL,deleted INTEGER NOT NULL,payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS description_changes_epoch ON description_changes(epoch,sequence);
            CREATE INDEX IF NOT EXISTS description_changes_version ON description_changes(epoch,version_id);
            CREATE TABLE IF NOT EXISTS description_delta_seen(epoch TEXT NOT NULL,version_id TEXT NOT NULL,PRIMARY KEY(epoch,version_id));
            CREATE TABLE IF NOT EXISTS description_delta_tombstones(epoch TEXT NOT NULL,version_id TEXT NOT NULL,PRIMARY KEY(epoch,version_id));
            CREATE TABLE IF NOT EXISTS description_outbox_order(
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                operation_id TEXT NOT NULL UNIQUE REFERENCES description_outbox(operation_id) ON DELETE CASCADE);
            INSERT OR IGNORE INTO description_outbox_order(operation_id) SELECT operation_id FROM description_outbox ORDER BY rowid;
            CREATE TRIGGER IF NOT EXISTS description_outbox_sequence AFTER INSERT ON description_outbox BEGIN
                INSERT OR IGNORE INTO description_outbox_order(operation_id) VALUES(NEW.operation_id);
            END;
            INSERT INTO description_changes(epoch,version_id,deleted,payload)
                SELECT epoch,version_id,0,payload FROM description_versions WHERE NOT EXISTS(SELECT 1 FROM description_changes);
            CREATE TRIGGER IF NOT EXISTS description_change_insert AFTER INSERT ON description_versions BEGIN
                INSERT INTO description_changes(epoch,version_id,deleted,payload) VALUES(NEW.epoch,NEW.version_id,0,NEW.payload);
            END;
            CREATE TRIGGER IF NOT EXISTS description_change_delete AFTER DELETE ON description_versions BEGIN
                DELETE FROM description_delta_seen WHERE epoch=OLD.epoch AND version_id=OLD.version_id;
                UPDATE description_changes SET deleted=1,payload=json_object('VersionId',OLD.version_id,'Kind',OLD.kind,'MediaId',OLD.media_id)
                    WHERE epoch=OLD.epoch AND version_id=OLD.version_id;
                INSERT INTO description_changes(epoch,version_id,deleted,payload)
                    VALUES(OLD.epoch,OLD.version_id,1,json_object('VersionId',OLD.version_id,'Kind',OLD.kind,'MediaId',OLD.media_id));
            END;
            CREATE TABLE IF NOT EXISTS description_delta_checkpoint(
                id INTEGER PRIMARY KEY CHECK(id=1),epoch TEXT NOT NULL,scope TEXT NOT NULL,sequence INTEGER NOT NULL);
            PRAGMA user_version=94;
            """;
        command.ExecuteNonQuery(); transaction.Commit();
    }
}
