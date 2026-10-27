using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class VideoMetadataOwnershipMigration
{
    internal static void Apply(SqliteConnection connection)
    {
        using var transaction=connection.BeginTransaction();
        using var command=connection.CreateCommand(); command.Transaction=transaction;
        // A remote GUI mirrors paths and media, not source-binding credentials.
        // Both photo and video decisions must recognize its publisher namespace.
        command.CommandText = "DROP VIEW IF EXISTS sync_image_owners;" + ClassificationOwnershipMigration.OwnerViewSql;
        command.ExecuteNonQuery();
        command.CommandText="""
            CREATE INDEX IF NOT EXISTS ix_metadata_edits_operation ON metadata_edits(operation_id);
            CREATE VIEW sync_video_owners AS
            SELECT v.id AS video_id,v.content_id,
                CASE
                    WHEN (SELECT COUNT(*) FROM sync_source_bindings b WHERE b.source_id=v.source_id)=1
                    THEN 'publisher:'||(SELECT b.publisher_id FROM sync_source_bindings b WHERE b.source_id=v.source_id LIMIT 1)
                    WHEN (SELECT COUNT(*) FROM sync_source_bindings b WHERE b.source_id=v.source_id)>1
                    THEN 'ambiguous-source:'||v.source_id
                    WHEN lower(substr(v.path,1,8))='\\picer-' AND length(v.path)>41 AND substr(v.path,41,1)='\'
                        AND lower(substr(v.path,9,32)) NOT GLOB '*[^0-9a-f]*'
                    THEN 'publisher:'||lower(substr(v.path,9,32))
                    ELSE 'library:'||(SELECT library_id FROM library_identity WHERE id=1)
                END AS owner_key
            FROM videos v;
            CREATE TABLE video_legacy_metadata_owners(
                content_id INTEGER PRIMARY KEY REFERENCES video_contents(id) ON DELETE CASCADE,owner_key TEXT NOT NULL);
            INSERT INTO video_legacy_metadata_owners
                SELECT content_id,MIN(owner_key) FROM sync_video_owners
                GROUP BY content_id HAVING COUNT(DISTINCT owner_key)=1 AND MIN(owner_key) NOT LIKE 'ambiguous-source:%';
            CREATE TABLE video_metadata_ownership_reviews(
                operation_id TEXT PRIMARY KEY,content_id INTEGER NOT NULL REFERENCES video_contents(id) ON DELETE CASCADE,
                kind TEXT NOT NULL,field TEXT NOT NULL,payload TEXT NOT NULL,state TEXT NOT NULL DEFAULT 'pending',resolution_json TEXT);
            CREATE INDEX ix_video_ownership_review_state ON video_metadata_ownership_reviews(state);
            INSERT INTO video_metadata_ownership_reviews(operation_id,content_id,kind,field,payload)
            SELECT e.operation_id,v.content_id,e.kind,e.field,
                json_object('Kind',e.kind,'EntityId',e.entity_id,'Field',e.field,'Value',e.value,'UpdatedUtc',e.updated_utc,
                    'OperationId',e.operation_id,'VideoContentIdentity',e.video_content_identity,'ServerVersion',e.server_version,'BaseVersion',e.base_version)
            FROM metadata_edits e JOIN videos v ON v.id=e.entity_id
            WHERE e.kind IN ('video-tag-link','video-concept')
                AND (e.video_content_identity='sha256:'||(SELECT lower(sha256) FROM video_contents WHERE id=v.content_id)
                    OR e.video_content_identity='local:'||(SELECT epoch FROM mirror_revision WHERE id=1)||':'||v.content_id)
                AND (EXISTS(SELECT 1 FROM sync_video_owners ambiguous WHERE ambiguous.content_id=v.content_id AND ambiguous.owner_key LIKE 'ambiguous-source:%') OR EXISTS(
                SELECT 1 FROM sync_video_owners a JOIN sync_video_owners b ON b.content_id=a.content_id AND b.owner_key<>a.owner_key
                WHERE a.content_id=v.content_id));
            INSERT INTO video_metadata_ownership_reviews(operation_id,content_id,kind,field,payload)
            SELECT lower(hex(randomblob(16))),t.video_id,'video-tag-link',CAST(t.tag_id AS TEXT),
                json_object('Kind','video-tag-link','EntityId',(SELECT MIN(video_id) FROM sync_video_owners WHERE content_id=t.video_id),
                    'Field',CAST(t.tag_id AS TEXT),'Value','1','UpdatedUtc',strftime('%Y-%m-%dT%H:%M:%f','now')||'0000Z',
                    'VideoContentIdentity',CASE WHEN c.sha256 IS NOT NULL THEN 'sha256:'||lower(c.sha256)
                        ELSE 'local:'||(SELECT epoch FROM mirror_revision WHERE id=1)||':'||c.id END)
            FROM video_tags t JOIN video_contents c ON c.id=t.video_id
            WHERE NOT EXISTS(SELECT 1 FROM video_legacy_metadata_owners WHERE content_id=t.video_id)
                AND EXISTS(SELECT 1 FROM sync_video_owners WHERE content_id=t.video_id)
                AND NOT EXISTS(SELECT 1 FROM video_metadata_ownership_reviews r WHERE r.content_id=t.video_id
                    AND r.kind='video-tag-link' AND r.field=CAST(t.tag_id AS TEXT));
            INSERT INTO video_metadata_ownership_reviews(operation_id,content_id,kind,field,payload)
            SELECT lower(hex(randomblob(16))),t.video_id,'video-concept',json_array(t.concept_id,t.model_version),
                json_object('Kind','video-concept','EntityId',(SELECT MIN(video_id) FROM sync_video_owners WHERE content_id=t.video_id),
                    'Field',json_array(t.concept_id,t.model_version)||'','Value',t.state,
                    'UpdatedUtc',strftime('%Y-%m-%dT%H:%M:%f','now')||'0000Z',
                    'VideoContentIdentity',CASE WHEN c.sha256 IS NOT NULL THEN 'sha256:'||lower(c.sha256)
                        ELSE 'local:'||(SELECT epoch FROM mirror_revision WHERE id=1)||':'||c.id END)
            FROM video_visual_concepts t JOIN video_contents c ON c.id=t.video_id
            WHERE NOT EXISTS(SELECT 1 FROM video_legacy_metadata_owners WHERE content_id=t.video_id)
                AND EXISTS(SELECT 1 FROM sync_video_owners WHERE content_id=t.video_id)
                AND NOT EXISTS(SELECT 1 FROM video_metadata_ownership_reviews r WHERE r.content_id=t.video_id
                    AND r.kind='video-concept' AND json_extract(r.field,'$[0]')=t.concept_id AND json_extract(r.field,'$[1]')=t.model_version);
            UPDATE video_metadata_ownership_reviews SET payload=json_set(payload,'$.OperationId',operation_id)
                WHERE json_extract(payload,'$.OperationId') IS NULL;
            DELETE FROM metadata_edits WHERE operation_id IN (SELECT operation_id FROM video_metadata_ownership_reviews);

            CREATE VIEW video_owned_metadata_edits AS
            SELECT * FROM (
                SELECT e.*,o.content_id,o.owner_key,
                    ROW_NUMBER() OVER(PARTITION BY o.content_id,o.owner_key,e.kind,
                        CASE WHEN e.kind='video-concept' THEN json_array(json_extract(e.field,'$[0]'),json_extract(e.field,'$[1]')) ELSE e.field END
                        ORDER BY MAX(e.server_version,e.base_version) DESC,(e.server_version=0) DESC,e.updated_utc DESC,e.operation_id DESC) AS owner_rank
                FROM metadata_edits e JOIN sync_video_owners o ON o.video_id=e.entity_id
                WHERE e.kind IN ('video-tag-link','video-concept')
                    AND o.owner_key NOT LIKE 'ambiguous-source:%'
                    AND (e.video_content_identity='sha256:'||(SELECT lower(sha256) FROM video_contents WHERE id=o.content_id)
                        OR e.video_content_identity='local:'||(SELECT epoch FROM mirror_revision WHERE id=1)||':'||o.content_id)
            ) WHERE owner_rank=1;
            CREATE VIEW video_effective_tags AS
            SELECT o.video_id,CAST(e.field AS INTEGER) AS tag_id
            FROM sync_video_owners o JOIN video_owned_metadata_edits e
                ON e.content_id=o.content_id AND e.owner_key=o.owner_key AND e.kind='video-tag-link' AND e.value='1'
            WHERE EXISTS(SELECT 1 FROM tags WHERE id=CAST(e.field AS INTEGER))
            UNION
            SELECT o.video_id,t.tag_id FROM sync_video_owners o JOIN video_tags t ON t.video_id=o.content_id
            WHERE EXISTS(SELECT 1 FROM video_legacy_metadata_owners legacy WHERE legacy.content_id=o.content_id AND legacy.owner_key=o.owner_key)
                AND NOT EXISTS(SELECT 1 FROM video_owned_metadata_edits e WHERE e.content_id=o.content_id AND e.owner_key=o.owner_key
                    AND e.kind='video-tag-link' AND e.field=CAST(t.tag_id AS TEXT));
            CREATE VIEW video_effective_concepts AS
            SELECT o.video_id,json_extract(e.field,'$[0]') AS concept_id,json_extract(e.field,'$[1]') AS model_version,e.value AS state
            FROM sync_video_owners o JOIN video_owned_metadata_edits e
                ON e.content_id=o.content_id AND e.owner_key=o.owner_key AND e.kind='video-concept'
            UNION
            SELECT o.video_id,c.concept_id,c.model_version,c.state FROM sync_video_owners o JOIN video_visual_concepts c ON c.video_id=o.content_id
            WHERE EXISTS(SELECT 1 FROM video_legacy_metadata_owners legacy WHERE legacy.content_id=o.content_id AND legacy.owner_key=o.owner_key)
                AND NOT EXISTS(SELECT 1 FROM video_owned_metadata_edits e WHERE e.content_id=o.content_id AND e.owner_key=o.owner_key
                    AND e.kind='video-concept' AND json_extract(e.field,'$[0]')=c.concept_id AND json_extract(e.field,'$[1]')=c.model_version);
            """;
        command.ExecuteNonQuery();
        foreach (var operation in new[]{"DELETE","UPDATE OF content_id,source_id,path"})
        {
            var name=operation=="DELETE"?"delete":"update";
            var guard=operation=="DELETE"?"":"WHEN OLD.content_id IS NEW.content_id OR (SELECT sha256 FROM video_contents WHERE id=OLD.content_id) IS NOT NULL OR (SELECT sha256 FROM video_contents WHERE id=NEW.content_id) IS NULL";
            const string orphan="EXISTS(SELECT 1 FROM video_legacy_metadata_owners legacy WHERE legacy.content_id=OLD.content_id AND NOT EXISTS(SELECT 1 FROM sync_video_owners remaining WHERE remaining.content_id=legacy.content_id AND remaining.owner_key=legacy.owner_key))";
            command.CommandText=$"""
                CREATE TRIGGER space_sync_video_legacy_purge_{name} AFTER {operation} ON videos
                {guard}
                BEGIN
                    DELETE FROM video_tags WHERE video_id=OLD.content_id AND {orphan};
                    DELETE FROM video_visual_concepts WHERE video_id=OLD.content_id AND {orphan};
                    DELETE FROM video_legacy_metadata_owners WHERE content_id=OLD.content_id AND {orphan};
                END;
                """;
            command.ExecuteNonQuery();
        }
        command.CommandText="""
            CREATE TRIGGER space_sync_video_verify_legacy AFTER UPDATE OF content_id ON videos
            WHEN OLD.content_id IS NOT NULL AND NEW.content_id<>OLD.content_id
                AND (SELECT sha256 FROM video_contents WHERE id=OLD.content_id) IS NULL
                AND (SELECT sha256 FROM video_contents WHERE id=NEW.content_id) IS NOT NULL
            BEGIN
                INSERT INTO metadata_edits(kind,entity_id,field,value,updated_utc,operation_id,video_content_identity)
                SELECT 'video-tag-link',NEW.id,CAST(t.tag_id AS TEXT),'1',strftime('%Y-%m-%dT%H:%M:%f','now')||'0000Z',
                    lower(hex(randomblob(16))),'sha256:'||(SELECT lower(sha256) FROM video_contents WHERE id=NEW.content_id)
                FROM video_tags t WHERE t.video_id=OLD.content_id AND EXISTS(
                    SELECT 1 FROM video_legacy_metadata_owners legacy JOIN sync_video_owners current
                        ON current.video_id=NEW.id AND current.owner_key=legacy.owner_key WHERE legacy.content_id=OLD.content_id)
                ON CONFLICT(kind,entity_id,field) DO NOTHING;
                INSERT INTO metadata_edits(kind,entity_id,field,value,updated_utc,operation_id,video_content_identity)
                SELECT 'video-concept',NEW.id,json_array(c.concept_id,c.model_version),c.state,strftime('%Y-%m-%dT%H:%M:%f','now')||'0000Z',
                    lower(hex(randomblob(16))),'sha256:'||(SELECT lower(sha256) FROM video_contents WHERE id=NEW.content_id)
                FROM video_visual_concepts c WHERE c.video_id=OLD.content_id AND EXISTS(
                    SELECT 1 FROM video_legacy_metadata_owners legacy JOIN sync_video_owners current
                        ON current.video_id=NEW.id AND current.owner_key=legacy.owner_key WHERE legacy.content_id=OLD.content_id)
                    AND NOT EXISTS(SELECT 1 FROM metadata_edits e WHERE e.kind='video-concept' AND e.entity_id=NEW.id
                        AND json_extract(e.field,'$[0]')=c.concept_id AND json_extract(e.field,'$[1]')=c.model_version)
                ON CONFLICT(kind,entity_id,field) DO NOTHING;
                INSERT INTO video_metadata_ownership_reviews(operation_id,content_id,kind,field,payload)
                SELECT lower(hex(randomblob(16))),NEW.content_id,'video-tag-link',CAST(t.tag_id AS TEXT),
                    json_object('Kind','video-tag-link','EntityId',NEW.id,'Field',CAST(t.tag_id AS TEXT),'Value','1',
                        'UpdatedUtc',strftime('%Y-%m-%dT%H:%M:%f','now')||'0000Z','VideoContentIdentity','sha256:'||(SELECT lower(sha256) FROM video_contents WHERE id=NEW.content_id))
                FROM video_tags t WHERE t.video_id=OLD.content_id AND NOT EXISTS(
                    SELECT 1 FROM video_legacy_metadata_owners legacy JOIN sync_video_owners current
                        ON current.video_id=NEW.id AND current.owner_key=legacy.owner_key WHERE legacy.content_id=OLD.content_id);
                INSERT INTO video_metadata_ownership_reviews(operation_id,content_id,kind,field,payload)
                SELECT lower(hex(randomblob(16))),NEW.content_id,'video-concept',json_array(c.concept_id,c.model_version),
                    json_object('Kind','video-concept','EntityId',NEW.id,'Field',json_array(c.concept_id,c.model_version)||'','Value',c.state,
                        'UpdatedUtc',strftime('%Y-%m-%dT%H:%M:%f','now')||'0000Z','VideoContentIdentity','sha256:'||(SELECT lower(sha256) FROM video_contents WHERE id=NEW.content_id))
                FROM video_visual_concepts c WHERE c.video_id=OLD.content_id AND NOT EXISTS(
                    SELECT 1 FROM video_legacy_metadata_owners legacy JOIN sync_video_owners current
                        ON current.video_id=NEW.id AND current.owner_key=legacy.owner_key WHERE legacy.content_id=OLD.content_id);
                UPDATE video_metadata_ownership_reviews SET payload=json_set(payload,'$.OperationId',operation_id)
                    WHERE content_id=NEW.content_id AND json_extract(payload,'$.OperationId') IS NULL;
                DELETE FROM video_tags WHERE video_id=OLD.content_id;
                DELETE FROM video_visual_concepts WHERE video_id=OLD.content_id;
            END;
            """;
        command.ExecuteNonQuery();
        foreach (var table in new[]{"video_tags","video_visual_concepts"})
        {
            command.CommandText=$"""
                CREATE TRIGGER space_sync_legacy_owner_{table} AFTER INSERT ON {table}
                BEGIN
                    INSERT OR IGNORE INTO video_legacy_metadata_owners(content_id,owner_key)
                    SELECT content_id,MIN(owner_key) FROM sync_video_owners WHERE content_id=NEW.video_id
                    GROUP BY content_id HAVING COUNT(DISTINCT owner_key)=1 AND MIN(owner_key) NOT LIKE 'ambiguous-source:%';
                END;
                """;
            command.ExecuteNonQuery();
        }
        foreach (var operation in new[]{"INSERT","UPDATE","DELETE"})
        {
            var row=operation=="DELETE"?"OLD":"NEW";
            command.CommandText=$"""
                CREATE TRIGGER space_sync_owned_video_edit_{operation} AFTER {operation} ON metadata_edits
                WHEN {row}.kind IN ('video-tag-link','video-concept') AND NOT EXISTS(SELECT 1 FROM sync_suppression WHERE id=1)
                BEGIN
                    UPDATE mirror_revision SET metadata_revision=metadata_revision+1 WHERE id=1;
                    UPDATE sync_identity_offer_revision SET revision=revision+1 WHERE id=1 AND {row}.kind='video-tag-link';
                    INSERT INTO sync_publisher_notifications(publisher_id,revision)
                    SELECT DISTINCT e.publisher_id,1 FROM sync_entity_identities e
                        JOIN sync_video_owners target ON target.video_id=e.target_id AND e.kind='video'
                        JOIN sync_video_owners origin ON origin.video_id={row}.entity_id
                    WHERE target.content_id=origin.content_id AND target.owner_key=origin.owner_key
                    ON CONFLICT(publisher_id) DO UPDATE SET revision=revision+1;
                END;
                """;
            command.ExecuteNonQuery();
        }
        command.CommandText="PRAGMA user_version=63"; command.ExecuteNonQuery(); transaction.Commit();
    }
}
