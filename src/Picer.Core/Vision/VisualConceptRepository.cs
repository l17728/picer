using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.Core.Vision;

public sealed class VisualConceptRepository(PicerDatabase database)
{
    public IReadOnlyList<VisualConceptScore> GetScores(long imageId)
    {
        using var command = database.Command("""
            SELECT concept_id, MAX(confidence) FROM image_visual_concepts
            WHERE image_id=$image AND state IN ('suggested','accepted') GROUP BY concept_id
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
        }
        UpsertAnalysis(image, analyzer, "completed", null, transaction);
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
        using var transaction = database.BeginTransaction();
        using (var reject = database.Command("INSERT OR REPLACE INTO visual_concept_rejections(image_id,concept_id,rejected_utc) VALUES($image,$concept,$now)"))
        {
            reject.Transaction = transaction;
            reject.Parameters.AddWithValue("$image", imageId);
            reject.Parameters.AddWithValue("$concept", conceptId);
            reject.Parameters.AddWithValue("$now", Format(DateTime.UtcNow));
            reject.ExecuteNonQuery();
        }
        using (var remove = database.Command("DELETE FROM image_visual_concepts WHERE image_id=$image AND concept_id=$concept"))
        {
            remove.Transaction = transaction;
            remove.Parameters.AddWithValue("$image", imageId);
            remove.Parameters.AddWithValue("$concept", conceptId);
            remove.ExecuteNonQuery();
        }
        transaction.Commit();
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
