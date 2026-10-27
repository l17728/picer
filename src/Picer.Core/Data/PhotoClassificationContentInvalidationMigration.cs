using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class PhotoClassificationContentInvalidationMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction=connection.BeginTransaction();using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="""
            CREATE TRIGGER IF NOT EXISTS photo_classification_content_invalidate AFTER UPDATE OF sha256,file_size,modified_utc ON images
            WHEN (length(COALESCE(OLD.sha256,''))=64 AND lower(OLD.sha256) NOT GLOB '*[^0-9a-f]*'
                    AND lower(NEW.sha256) IS NOT lower(OLD.sha256))
                OR (NOT (length(COALESCE(OLD.sha256,''))=64 AND lower(OLD.sha256) NOT GLOB '*[^0-9a-f]*')
                    AND (NEW.file_size IS NOT OLD.file_size OR NEW.modified_utc IS NOT OLD.modified_utc))
            BEGIN
                DELETE FROM image_classification_assertions WHERE image_id=NEW.id;
                DELETE FROM image_classification_rejections WHERE image_id=NEW.id;
                DELETE FROM visual_concept_rejections WHERE image_id=NEW.id;
                DELETE FROM image_visual_concepts WHERE image_id=NEW.id;
                DELETE FROM semantic_content_cluster_members WHERE image_id=NEW.id;
            END;
            DELETE FROM image_classification_assertions AS a
            WHERE length(a.content_sha256)=64 AND EXISTS(
                SELECT 1 FROM images i WHERE i.id=a.image_id AND length(i.sha256)=64
                    AND lower(i.sha256) NOT GLOB '*[^0-9a-f]*' AND lower(i.sha256)<>lower(a.content_sha256));
            PRAGMA user_version=76;
            """;
        command.ExecuteNonQuery();transaction.Commit();
    }
}
