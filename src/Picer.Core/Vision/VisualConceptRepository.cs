using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.Core.Vision;

public sealed class VisualConceptRepository(PicerDatabase database)
{
    public IReadOnlyList<VisualConceptScore> GetScores(long imageId)
    {
        using var command = database.Command("""
            SELECT concept_id, MAX(confidence) FROM image_classification_assertions
            WHERE image_id=$image AND source_kind='vision_model'
              AND assertion_state IN ('suggested','automatic') AND review_state IN ('pending','accepted')
              AND NOT EXISTS(SELECT 1 FROM image_classification_rejections r
                  WHERE r.concept_id=image_classification_assertions.concept_id AND r.source_scope IN ('vision_model','all')
                    AND (r.image_id=$image OR (r.content_sha256=image_classification_assertions.content_sha256
                        AND r.owner_key=(SELECT owner_key FROM sync_image_owners WHERE image_id=$image))))
            GROUP BY concept_id
            ORDER BY confidence DESC
            """);
        command.Parameters.AddWithValue("$image", imageId);
        using var reader = command.ExecuteReader();
        var scores = new List<VisualConceptScore>();
        while (reader.Read()) scores.Add(new VisualConceptScore(reader.GetString(0), reader.GetFloat(1)));
        return scores;
    }

    public bool IsCurrent(ImageRecord image, IImageSemanticAnalyzer analyzer)
    {
        using var command = database.Command("""
            SELECT 1 FROM vision_analyses
            WHERE image_id=$image AND analyzer=$analyzer AND model_version=$version
              AND source_file_size=$size AND source_modified_utc=$modified AND status='completed'
            """);
        command.Parameters.AddWithValue("$image", image.Id);
        command.Parameters.AddWithValue("$analyzer", analyzer.AnalyzerName);
        command.Parameters.AddWithValue("$version", analyzer.ModelVersion);
        command.Parameters.AddWithValue("$size", image.FileSize);
        command.Parameters.AddWithValue("$modified", Format(image.ModifiedUtc));
        return command.ExecuteScalar() is not null;
    }

    public void Save(ImageRecord image, IImageSemanticAnalyzer analyzer, SemanticAnalysisResult result)
    {
        using var transaction = database.BeginTransaction();
        using (var clear = database.Command("DELETE FROM image_visual_concepts WHERE image_id=$image AND model_version=$version"))
        {
            clear.Transaction = transaction;
            clear.Parameters.AddWithValue("$image", image.Id);
            clear.Parameters.AddWithValue("$version", analyzer.ModelVersion);
            clear.ExecuteNonQuery();
        }
        using (var clearAssertions = database.Command("DELETE FROM image_classification_assertions WHERE image_id=$image AND source_kind='vision_model' AND source_version=$version"))
        {
            clearAssertions.Transaction = transaction;
            clearAssertions.Parameters.AddWithValue("$image", image.Id);
            clearAssertions.Parameters.AddWithValue("$version", analyzer.ModelVersion);
            clearAssertions.ExecuteNonQuery();
        }
        PromoteImageRejections(image.Id, NormalizeHash(image.Sha256), transaction);
        foreach (var concept in result.Concepts
                     .Where(value => value.Confidence is >= 0 and <= 1)
                     .GroupBy(value => value.ConceptId, StringComparer.Ordinal)
                     .Select(group => group.MaxBy(value => value.Confidence)!))
        {
            using var insert = database.Command("""
                INSERT INTO image_visual_concepts(image_id,concept_id,model_version,confidence,state,created_utc)
                SELECT $image,$concept,$version,$confidence,'suggested',$now
                WHERE NOT EXISTS (SELECT 1 FROM visual_concept_rejections WHERE image_id=$image AND concept_id=$concept)
                """);
            insert.Transaction = transaction;
            insert.Parameters.AddWithValue("$image", image.Id);
            insert.Parameters.AddWithValue("$concept", concept.ConceptId);
            insert.Parameters.AddWithValue("$version", analyzer.ModelVersion);
            insert.Parameters.AddWithValue("$confidence", concept.Confidence);
            insert.Parameters.AddWithValue("$now", Format(DateTime.UtcNow));
            insert.ExecuteNonQuery();

            using var assertion = database.Command("""
                INSERT INTO image_classification_assertions(
                    image_id,content_sha256,concept_id,source_kind,source_version,assertion_state,review_state,
                    confidence,evidence_fingerprint,created_utc,updated_utc)
                SELECT $image,$sha,$concept,'vision_model',$version,'suggested','pending',$confidence,$version,$now,$now
                WHERE NOT EXISTS (
                    SELECT 1 FROM image_classification_rejections r
                    WHERE r.concept_id=$concept AND r.source_scope IN ('vision_model','all')
                      AND (r.image_id=$image OR ($sha IS NOT NULL AND r.content_sha256=$sha
                          AND r.owner_key=(SELECT owner_key FROM sync_image_owners WHERE image_id=$image))))
                """);
            assertion.Transaction = transaction;
            assertion.Parameters.AddWithValue("$image", image.Id);
            assertion.Parameters.AddWithValue("$sha", (object?)NormalizeHash(image.Sha256) ?? DBNull.Value);
            assertion.Parameters.AddWithValue("$concept", concept.ConceptId);
            assertion.Parameters.AddWithValue("$version", analyzer.ModelVersion);
            assertion.Parameters.AddWithValue("$confidence", concept.Confidence);
            assertion.Parameters.AddWithValue("$now", Format(DateTime.UtcNow));
            assertion.ExecuteNonQuery();
        }
        UpsertAnalysis(image, analyzer, "completed", null, transaction);
        database.ReplayPhotoClassificationFields(image.Id,transaction);
        transaction.Commit();
    }

    public void SaveFailure(ImageRecord image, IImageSemanticAnalyzer analyzer, string errorCode)
    {
        using var transaction = database.BeginTransaction();
        UpsertAnalysis(image, analyzer, "failed", errorCode, transaction);
        transaction.Commit();
    }

    public void Reject(long imageId, string conceptId)
    {
        using var transaction=database.BeginTransaction();
        ClassificationOwnershipReview.Record(database,transaction,imageId,conceptId,"rejected");
        database.RecordPhotoClassificationDecision(imageId,conceptId,"rejected",false,transaction);
        transaction.Commit();
    }

    public void Confirm(long imageId, string conceptId)
    {
        using var transaction=database.BeginTransaction();
        ClassificationOwnershipReview.Record(database,transaction,imageId,conceptId,"confirmed");
        database.RecordPhotoClassificationDecision(imageId,conceptId,"1",true,transaction);
        database.RecordPhotoClassificationDecision(imageId,conceptId,"accepted",false,transaction);
        transaction.Commit();
    }

    public void RemoveMachineClassification(long imageId, string conceptId)
    {
        using var transaction=database.BeginTransaction();
        database.RecordPhotoClassificationDecision(imageId,conceptId,"retracted",false,transaction);
        transaction.Commit();
    }

    public void Correct(long imageId, string oldConceptId, string newConceptId)
    {
        if(string.Equals(oldConceptId,newConceptId,StringComparison.Ordinal)) { Confirm(imageId,newConceptId);return; }
        using var transaction=database.BeginTransaction();
        ClassificationOwnershipReview.Record(database,transaction,imageId,oldConceptId,"rejected");
        ClassificationOwnershipReview.Record(database,transaction,imageId,newConceptId,"confirmed");
        database.RecordPhotoClassificationDecision(imageId,oldConceptId,"rejected",false,transaction);
        database.RecordPhotoClassificationDecision(imageId,newConceptId,"1",true,transaction);
        database.RecordPhotoClassificationDecision(imageId,newConceptId,"accepted",false,transaction);
        transaction.Commit();
    }

    private string? GetVerifiedHash(long imageId, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        using var command = database.Command("SELECT lower(sha256) FROM images WHERE id=$image");
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$image", imageId);
        return NormalizeHash(command.ExecuteScalar() as string);
    }

    private void ClearOwnedRejection(long imageId, string? hash, string conceptId,
        Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        using var clear = database.Command("""
            DELETE FROM image_classification_rejections WHERE concept_id=$concept AND source_scope IN ('vision_model','all')
              AND (image_id=$image OR ($sha IS NOT NULL AND content_sha256=$sha
                AND owner_key=(SELECT owner_key FROM sync_image_owners WHERE image_id=$image)));
            DELETE FROM visual_concept_rejections WHERE concept_id=$concept AND (image_id=$image OR (
                $sha IS NOT NULL AND image_id IN (SELECT i.id FROM images i JOIN sync_image_owners o ON o.image_id=i.id
                    WHERE lower(COALESCE(i.content_sha256,i.sha256))=$sha
                        AND o.owner_key=(SELECT owner_key FROM sync_image_owners WHERE image_id=$image))));
            """);
        clear.Transaction = transaction; clear.Parameters.AddWithValue("$image", imageId);
        clear.Parameters.AddWithValue("$sha", (object?)hash ?? DBNull.Value); clear.Parameters.AddWithValue("$concept", conceptId);
        clear.ExecuteNonQuery();
    }

    private static string? NormalizeHash(string? hash) =>
        !string.IsNullOrWhiteSpace(hash) && hash.Length == 64 && hash.All(Uri.IsHexDigit)
            ? hash.ToLowerInvariant()
            : null;

    private void PromoteImageRejections(long imageId, string? hash, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        if (hash is null) return;
        using (var promote = database.Command("""
            INSERT INTO image_classification_rejections(image_id,content_sha256,concept_id,source_scope,rejected_utc,owner_key)
            SELECT NULL,$sha,concept_id,source_scope,rejected_utc,
                (SELECT owner_key FROM sync_image_owners WHERE image_id=$image)
            FROM image_classification_rejections WHERE image_id=$image
            ON CONFLICT DO NOTHING
            """))
        {
            promote.Transaction = transaction;
            promote.Parameters.AddWithValue("$sha", hash);
            promote.Parameters.AddWithValue("$image", imageId);
            promote.ExecuteNonQuery();
        }
        using (var update = database.Command("UPDATE image_classification_assertions SET content_sha256=$sha WHERE image_id=$image AND content_sha256 IS NULL"))
        {
            update.Transaction = transaction;
            update.Parameters.AddWithValue("$sha", hash);
            update.Parameters.AddWithValue("$image", imageId);
            update.ExecuteNonQuery();
        }
        using var remove = database.Command("DELETE FROM image_classification_rejections WHERE image_id=$image");
        remove.Transaction = transaction;
        remove.Parameters.AddWithValue("$image", imageId);
        remove.ExecuteNonQuery();
    }

    private void UpsertAnalysis(ImageRecord image, IImageSemanticAnalyzer analyzer, string status,
        string? errorCode, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        using var command = database.Command("""
            INSERT INTO vision_analyses(image_id,analyzer,model_version,source_file_size,source_modified_utc,status,face_count,error_code,analyzed_utc)
            VALUES($image,$analyzer,$version,$size,$modified,$status,0,$error,$now)
            ON CONFLICT(image_id,analyzer) DO UPDATE SET model_version=excluded.model_version,
              source_file_size=excluded.source_file_size,source_modified_utc=excluded.source_modified_utc,
              status=excluded.status,error_code=excluded.error_code,analyzed_utc=excluded.analyzed_utc
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$image", image.Id);
        command.Parameters.AddWithValue("$analyzer", analyzer.AnalyzerName);
        command.Parameters.AddWithValue("$version", analyzer.ModelVersion);
        command.Parameters.AddWithValue("$size", image.FileSize);
        command.Parameters.AddWithValue("$modified", Format(image.ModifiedUtc));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Format(DateTime.UtcNow));
        command.ExecuteNonQuery();
    }

    private static string Format(DateTime value) => value.ToUniversalTime().ToString("o");
}
