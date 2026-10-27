using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class PhotoClassificationIdentityMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction=connection.BeginTransaction();
        using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="""
            CREATE TABLE IF NOT EXISTS photo_metadata_file_versions(
                image_id INTEGER PRIMARY KEY REFERENCES images(id) ON DELETE CASCADE,
                version_id TEXT NOT NULL);
            INSERT OR IGNORE INTO photo_metadata_file_versions SELECT id,lower(hex(randomblob(16))) FROM images;
            CREATE TABLE IF NOT EXISTS photo_metadata_operation_bindings(
                operation_id TEXT PRIMARY KEY,reference_id INTEGER NOT NULL,
                owner_key TEXT NOT NULL,content_identity TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_photo_metadata_binding_reference ON photo_metadata_operation_bindings(reference_id);
            CREATE TRIGGER IF NOT EXISTS photo_metadata_file_version_insert AFTER INSERT ON images BEGIN
                INSERT INTO photo_metadata_file_versions VALUES(NEW.id,lower(hex(randomblob(16))));
            END;
            CREATE TRIGGER IF NOT EXISTS photo_metadata_file_version_update AFTER UPDATE OF file_size,modified_utc ON images
            WHEN NEW.file_size IS NOT OLD.file_size OR NEW.modified_utc IS NOT OLD.modified_utc BEGIN
                UPDATE photo_metadata_file_versions SET version_id=lower(hex(randomblob(16))) WHERE image_id=NEW.id;
            END;
            CREATE TRIGGER IF NOT EXISTS photo_metadata_bind_verified_hash AFTER UPDATE OF sha256 ON images
            WHEN length(NEW.sha256)=64 AND lower(NEW.sha256) NOT GLOB '*[^0-9a-f]*'
              AND NOT (length(COALESCE(OLD.sha256,''))=64 AND lower(OLD.sha256) NOT GLOB '*[^0-9a-f]*')
              AND NEW.file_size=OLD.file_size AND NEW.modified_utc=OLD.modified_utc BEGIN
                UPDATE photo_metadata_operation_bindings SET content_identity='sha256:'||lower(NEW.sha256)
                WHERE reference_id=NEW.id AND content_identity='local:'||(SELECT epoch FROM mirror_revision WHERE id=1)||':'||
                    (SELECT version_id FROM photo_metadata_file_versions WHERE image_id=NEW.id);
            END;
            CREATE TRIGGER IF NOT EXISTS photo_metadata_binding_purge BEFORE DELETE ON images BEGIN
                UPDATE photo_metadata_operation_bindings SET reference_id=COALESCE((
                    SELECT MIN(i.id) FROM images i JOIN sync_image_owners o ON o.image_id=i.id
                    WHERE i.id<>OLD.id AND o.owner_key=photo_metadata_operation_bindings.owner_key
                      AND 'sha256:'||lower(i.sha256)=photo_metadata_operation_bindings.content_identity),reference_id)
                WHERE reference_id=OLD.id;
                DELETE FROM photo_metadata_operation_bindings WHERE reference_id=OLD.id;
            END;
            PRAGMA user_version=74;
            """;
        command.ExecuteNonQuery();transaction.Commit();
    }
}
