using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

internal static class PhotoClassificationLegacySeed
{
    internal static void Apply(SqliteConnection connection,SqliteTransaction transaction)
    {
        using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="""
            CREATE TEMP TABLE photo_legacy_decisions(kind TEXT,image_id INTEGER,concept TEXT,value TEXT,utc TEXT);
            CREATE TEMP TABLE photo_legacy_assertions AS
            SELECT a.*,CASE WHEN length(a.content_sha256)=64 AND lower(a.content_sha256) NOT GLOB '*[^0-9a-f]*'
                AND length(i.sha256)=64 AND lower(i.sha256) NOT GLOB '*[^0-9a-f]*' AND lower(a.content_sha256)<>lower(i.sha256)
                THEN (SELECT MIN(s.id) FROM images s JOIN sync_image_owners o ON o.image_id=s.id
                    WHERE lower(s.sha256)=lower(a.content_sha256)
                        AND o.owner_key=(SELECT owner_key FROM sync_image_owners WHERE image_id=i.id))
                ELSE a.image_id END AS bound_image_id
            FROM image_classification_assertions a JOIN images i ON i.id=a.image_id;
            INSERT INTO photo_legacy_decisions
            SELECT 'photo-classification-confirmation',bound_image_id,concept_id,
                CASE WHEN review_state='accepted' THEN '1' ELSE '0' END,COALESCE(reviewed_utc,updated_utc)
            FROM temp.photo_legacy_assertions WHERE source_kind='user' AND review_state IN ('accepted','retracted');
            INSERT INTO photo_legacy_decisions
            SELECT 'photo-classification-policy',bound_image_id,concept_id,review_state,reviewed_utc
            FROM temp.photo_legacy_assertions WHERE source_kind='vision_model'
                AND reviewed_utc IS NOT NULL AND review_state IN ('accepted','rejected','retracted');
            INSERT INTO photo_legacy_decisions
            SELECT 'photo-classification-policy',i.id,r.concept_id,'rejected',r.rejected_utc
            FROM image_classification_rejections r JOIN images i
                ON r.image_id=i.id OR (r.image_id IS NULL AND r.content_sha256=lower(i.sha256)
                    AND r.owner_key=(SELECT owner_key FROM sync_image_owners WHERE image_id=i.id))
            WHERE r.source_scope IN ('vision_model','all');
            INSERT INTO photo_legacy_decisions
            SELECT 'photo-classification-policy',image_id,concept_id,'rejected',rejected_utc FROM visual_concept_rejections;
            INSERT INTO metadata_edits(kind,entity_id,field,value,updated_utc,operation_id,server_version,base_version)
            SELECT kind,image_id,concept,value,utc,lower(hex(randomblob(16))),0,0 FROM (
                SELECT d.*,ROW_NUMBER() OVER(PARTITION BY d.kind,d.image_id,d.concept ORDER BY d.utc DESC,d.value DESC) AS position
                FROM temp.photo_legacy_decisions d JOIN sync_image_owners o ON o.image_id=d.image_id
                WHERE o.owner_key NOT LIKE 'ambiguous-source:%')
            WHERE position=1
            ON CONFLICT(kind,entity_id,field) DO NOTHING;
            INSERT INTO photo_metadata_operation_bindings(operation_id,reference_id,owner_key,content_identity)
            SELECT e.operation_id,i.id,o.owner_key,
                CASE WHEN length(i.sha256)=64 AND lower(i.sha256) NOT GLOB '*[^0-9a-f]*' THEN 'sha256:'||lower(i.sha256)
                    ELSE 'local:'||(SELECT epoch FROM mirror_revision WHERE id=1)||':'||v.version_id END
            FROM metadata_edits e JOIN images i ON i.id=e.entity_id JOIN sync_image_owners o ON o.image_id=i.id
                JOIN photo_metadata_file_versions v ON v.image_id=i.id
            WHERE e.kind IN ('photo-classification-confirmation','photo-classification-policy')
                AND o.owner_key NOT LIKE 'ambiguous-source:%'
            ON CONFLICT(operation_id) DO NOTHING;
            INSERT INTO image_classification_assertions(image_id,content_sha256,concept_id,source_kind,source_version,
                assertion_state,review_state,confidence,evidence_ref,evidence_fingerprint,created_utc,updated_utc,reviewed_utc)
            SELECT bound_image_id,content_sha256,concept_id,source_kind,source_version,assertion_state,review_state,
                confidence,evidence_ref,evidence_fingerprint,created_utc,updated_utc,reviewed_utc
            FROM temp.photo_legacy_assertions WHERE bound_image_id IS NOT NULL AND bound_image_id<>image_id
                AND (source_kind='user' OR (source_kind='vision_model' AND reviewed_utc IS NOT NULL))
            ON CONFLICT(image_id,concept_id,source_kind,source_version) DO NOTHING;
            DROP TABLE temp.photo_legacy_decisions;
            DROP TABLE temp.photo_legacy_assertions;
            """;
        command.ExecuteNonQuery();
    }
}
