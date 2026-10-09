using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class VideoMetadataIdentityMigration
{
    public static void Apply(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE metadata_edits ADD COLUMN video_content_identity TEXT;
            UPDATE metadata_edits SET video_content_identity=COALESCE((
                SELECT CASE WHEN length(c.sha256)=64 AND lower(c.sha256) NOT GLOB '*[^0-9a-f]*'
                    THEN 'sha256:'||lower(c.sha256)
                    ELSE 'local:'||(SELECT epoch FROM mirror_revision WHERE id=1)||':'||c.id END
                FROM videos v JOIN video_contents c ON c.id=v.content_id WHERE v.id=metadata_edits.entity_id),
                'orphan:'||operation_id)
            WHERE kind IN ('video-tag-link','video-concept');
            CREATE TABLE video_metadata_operation_bindings(operation_id TEXT PRIMARY KEY,reference_id INTEGER NOT NULL,content_identity TEXT NOT NULL);
            CREATE INDEX idx_video_metadata_bindings_reference ON video_metadata_operation_bindings(reference_id);
            INSERT INTO video_metadata_operation_bindings SELECT operation_id,entity_id,video_content_identity FROM metadata_edits WHERE kind IN ('video-tag-link','video-concept');
            CREATE TRIGGER video_metadata_bind_verified_content AFTER UPDATE OF content_id ON videos
            WHEN EXISTS(SELECT 1 FROM video_contents WHERE id=new.content_id AND length(sha256)=64
                AND lower(sha256) NOT GLOB '*[^0-9a-f]*') BEGIN
                UPDATE metadata_edits SET video_content_identity='sha256:'||(SELECT lower(sha256) FROM video_contents WHERE id=new.content_id)
                WHERE kind IN ('video-tag-link','video-concept') AND entity_id=new.id
                    AND video_content_identity='local:'||(SELECT epoch FROM mirror_revision WHERE id=1)||':'||old.content_id;
                UPDATE video_metadata_operation_bindings SET content_identity='sha256:'||(SELECT lower(sha256) FROM video_contents WHERE id=new.content_id)
                WHERE reference_id=new.id AND content_identity='local:'||(SELECT epoch FROM mirror_revision WHERE id=1)||':'||old.content_id;
            END;
            PRAGMA user_version=45;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
