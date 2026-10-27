using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

/// <summary>Content decisions belong to a library locally and to one publisher on the service.</summary>
internal static class ClassificationOwnershipMigration
{
    internal const string OwnerViewSql = """
        CREATE VIEW IF NOT EXISTS sync_image_owners AS
        SELECT i.id AS image_id,
            (SELECT CASE WHEN COUNT(DISTINCT b.publisher_id)=1 THEN 'publisher:'||MIN(b.publisher_id)
                 WHEN COUNT(DISTINCT b.publisher_id)>1 THEN 'ambiguous-source:'||i.source_id
                 WHEN lower(substr(i.path,1,8))='\\picer-' AND length(i.path)>41 AND substr(i.path,41,1)='\'
                     AND lower(substr(i.path,9,32)) NOT GLOB '*[^0-9a-f]*'
                 THEN 'publisher:'||lower(substr(i.path,9,32))
                 ELSE 'library:'||(SELECT library_id FROM library_identity WHERE id=1) END
             FROM sync_source_bindings b WHERE b.source_id=i.source_id) AS owner_key
        FROM images i;
        """;

    internal static void Apply(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM pragma_table_info('image_classification_rejections') WHERE name='owner_key'";
        if (command.ExecuteScalar() is null)
        {
            command.CommandText = "ALTER TABLE image_classification_rejections ADD COLUMN owner_key TEXT NOT NULL DEFAULT ''";
            command.ExecuteNonQuery();
        }
        command.CommandText = OwnerViewSql + """
            CREATE INDEX IF NOT EXISTS idx_sync_source_bindings_source ON sync_source_bindings(source_id);
            DROP INDEX IF EXISTS idx_classification_rejections_content;
            CREATE UNIQUE INDEX idx_classification_rejections_content
                ON image_classification_rejections(owner_key,content_sha256,concept_id,source_scope)
                WHERE content_sha256 IS NOT NULL;
            CREATE INDEX IF NOT EXISTS idx_classification_rejections_owner ON image_classification_rejections(owner_key);
            CREATE TABLE IF NOT EXISTS classification_rejection_ownership_reviews(
                content_sha256 TEXT NOT NULL,concept_id TEXT NOT NULL,owner_key TEXT NOT NULL,
                decision TEXT NOT NULL CHECK(decision IN ('rejected','confirmed','restored')),
                reviewed_utc TEXT NOT NULL,PRIMARY KEY(content_sha256,concept_id,owner_key));
            """;
        command.ExecuteNonQuery();
        // A legacy hash without provenance is adopted only if all references have
        // one owner. An ambiguous legacy row stays preserved with an empty owner;
        // it is never broadcast to any publisher merely because its hash matches.
        command.CommandText = """
            UPDATE image_classification_rejections AS r SET owner_key=COALESCE((
                SELECT MIN(o.owner_key) FROM images i JOIN sync_image_owners o ON o.image_id=i.id
                WHERE lower(COALESCE(i.content_sha256,i.sha256))=lower(r.content_sha256)
                HAVING COUNT(DISTINCT o.owner_key)=1),'')
            WHERE r.content_sha256 IS NOT NULL AND r.owner_key='';
            """;
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT INTO image_classification_rejections(image_id,content_sha256,concept_id,source_scope,rejected_utc,owner_key)
            SELECT DISTINCT NULL,r.content_sha256,r.concept_id,r.source_scope,r.rejected_utc,o.owner_key
            FROM image_classification_rejections r
            JOIN images i ON lower(COALESCE(i.content_sha256,i.sha256))=lower(r.content_sha256)
            JOIN sync_image_owners o ON o.image_id=i.id
            JOIN image_classification_assertions a ON a.image_id=i.id AND a.concept_id=r.concept_id
            WHERE r.content_sha256 IS NOT NULL AND r.owner_key='' AND a.review_state='rejected'
                AND (a.source_kind='vision_model' OR r.source_scope='all')
            ON CONFLICT DO NOTHING;
            """;
        command.ExecuteNonQuery();
        foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            var row = operation == "DELETE" ? "OLD" : "NEW";
            string Payload(string alias) => $"json_object('image_id',{alias}.image_id,'content_sha256',{alias}.content_sha256,'concept_id',{alias}.concept_id,'source_scope',{alias}.source_scope,'rejected_utc',{alias}.rejected_utc,'owner_key',{alias}.owner_key)";
            var before = operation == "INSERT" ? "NULL" : Payload("OLD");
            var after = operation == "DELETE" ? "NULL" : Payload("NEW");
            command.CommandText = $"""
                DROP TRIGGER IF EXISTS space_sync_row_image_classification_rejections_{operation};
                CREATE TRIGGER space_sync_row_image_classification_rejections_{operation}
                AFTER {operation} ON image_classification_rejections
                WHEN NOT EXISTS(SELECT 1 FROM sync_suppression WHERE id=1)
                    AND EXISTS(SELECT 1 FROM sync_publishers){(operation == "UPDATE" ? $" AND {before} IS NOT {after}" : "")}
                BEGIN
                    INSERT INTO sync_metadata_row_journal(operation_id,table_name,row_key,operation,before_payload,after_payload)
                    VALUES(lower(hex(randomblob(16))),'image_classification_rejections',
                        json_array({row}.image_id,{row}.content_sha256,{row}.concept_id,{row}.source_scope,{row}.owner_key),
                        '{(operation == "DELETE" ? "delete" : "upsert")}',{before},{after});
                END;
                DROP TRIGGER IF EXISTS space_sync_image_classification_rejections_{operation.ToLowerInvariant()};
                CREATE TRIGGER space_sync_image_classification_rejections_{operation.ToLowerInvariant()}
                AFTER {operation} ON image_classification_rejections
                WHEN NOT EXISTS(SELECT 1 FROM sync_suppression WHERE id=1)
                BEGIN
                    INSERT INTO sync_metadata_changes(publisher_id,global_image_id,changed_utc)
                    SELECT e.publisher_id,e.global_id,strftime('%Y-%m-%dT%H:%M:%fZ','now')
                    FROM sync_entity_identities e JOIN images i ON e.kind='image' AND e.target_id=i.id
                    JOIN sync_image_owners o ON o.image_id=i.id
                    WHERE {row}.image_id=i.id OR ({row}.image_id IS NULL
                        AND {row}.owner_key=o.owner_key
                        AND lower({row}.content_sha256)=lower(COALESCE(i.content_sha256,i.sha256)));
                END;
                """;
            command.ExecuteNonQuery();
        }
        // Explicit purge drops the content decision when its last owned reference
        // goes away, even if another publisher still owns the same bytes.
        command.CommandText = """
            CREATE TRIGGER IF NOT EXISTS space_sync_owned_rejection_purge AFTER DELETE ON images
            BEGIN
                DELETE FROM classification_rejection_ownership_reviews
                WHERE lower(content_sha256)=lower(COALESCE(OLD.content_sha256,OLD.sha256))
                  AND NOT EXISTS(SELECT 1 FROM images i JOIN sync_image_owners o ON o.image_id=i.id
                    WHERE o.owner_key=classification_rejection_ownership_reviews.owner_key
                      AND lower(COALESCE(i.content_sha256,i.sha256))=lower(classification_rejection_ownership_reviews.content_sha256));
                DELETE FROM image_classification_rejections
                WHERE content_sha256 IS NOT NULL
                  AND lower(content_sha256)=lower(COALESCE(OLD.content_sha256,OLD.sha256))
                  AND NOT EXISTS(SELECT 1 FROM images i JOIN sync_image_owners o ON o.image_id=i.id
                    WHERE (image_classification_rejections.owner_key='' OR o.owner_key=image_classification_rejections.owner_key)
                      AND lower(COALESCE(i.content_sha256,i.sha256))=lower(image_classification_rejections.content_sha256));
                DELETE FROM sync_metadata_row_journal WHERE table_name='image_classification_rejections'
                  AND lower(COALESCE(json_extract(before_payload,'$.content_sha256'),json_extract(after_payload,'$.content_sha256')))=lower(COALESCE(OLD.content_sha256,OLD.sha256))
                  AND NOT EXISTS(SELECT 1 FROM images i JOIN sync_image_owners o ON o.image_id=i.id
                    WHERE (COALESCE(json_extract(before_payload,'$.owner_key'),json_extract(after_payload,'$.owner_key'),'')=''
                        OR o.owner_key=COALESCE(json_extract(before_payload,'$.owner_key'),json_extract(after_payload,'$.owner_key')))
                      AND lower(COALESCE(i.content_sha256,i.sha256))=lower(COALESCE(OLD.content_sha256,OLD.sha256)));
            END;
            INSERT INTO sync_metadata_changes(publisher_id,global_image_id,changed_utc)
            SELECT DISTINCT e.publisher_id,e.global_id,strftime('%Y-%m-%dT%H:%M:%fZ','now')
            FROM sync_entity_identities e JOIN images i ON e.kind='image' AND e.target_id=i.id
            JOIN sync_image_owners o ON o.image_id=i.id
            JOIN image_classification_rejections r ON r.owner_key=o.owner_key
                AND lower(r.content_sha256)=lower(COALESCE(i.content_sha256,i.sha256));
            PRAGMA user_version=60;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
