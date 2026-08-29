using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.Core.Vision;

public sealed class CloudVisionRepository(PicerDatabase database)
{
    public sealed record TaskRecord(long ImageId, string ImagePath, string Provider, string ModelVersion,
        string Status, string? Reason, string? Summary, string? ErrorCode, DateTime UpdatedUtc,
        long InputTokens, long OutputTokens, decimal EstimatedCostYuan);

    public IReadOnlyList<TaskRecord> GetRecentTasks(int limit = 500)
    {
        using var command = database.Command("""
            SELECT c.image_id,i.path,c.provider,c.model_version,c.status,c.reason,c.summary,c.error_code,c.updated_utc,
                   c.input_tokens,c.output_tokens,c.estimated_cost_yuan
            FROM cloud_vision_analyses c JOIN images i ON i.id=c.image_id
            ORDER BY c.updated_utc DESC LIMIT $limit
            """);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        using var reader = command.ExecuteReader();
        var records = new List<TaskRecord>();
        while (reader.Read()) records.Add(new TaskRecord(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
            DateTime.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind),
            reader.GetInt64(9), reader.GetInt64(10), reader.GetDecimal(11)));
        return records;
    }

    public decimal GetEstimatedCostSince(DateTime sinceUtc)
    {
        using var command = database.Command("SELECT COALESCE(SUM(estimated_cost_yuan),0) FROM cloud_vision_analyses WHERE status='completed' AND updated_utc >= $since");
        command.Parameters.AddWithValue("$since", sinceUtc.ToUniversalTime().ToString("o"));
        return Convert.ToDecimal(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<string> GetSuggestedTags(long imageId)
    {
        using var command = database.Command("""
            SELECT result_json FROM cloud_vision_analyses
            WHERE image_id=$image AND status='completed' AND result_json IS NOT NULL
            ORDER BY updated_utc DESC
            """);
        command.Parameters.AddWithValue("$image", imageId);
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            try
            {
                var result = JsonSerializer.Deserialize<CloudVisionResult>(reader.GetString(0),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (result is null) continue;
                foreach (var value in result.Scenes.Concat(result.Activities).Concat(result.Objects).Concat(result.SearchTerms))
                    if (!string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 40) tags.Add(value.Trim());
            }
            catch (JsonException) { }
        }
        using var rejected = database.Command("SELECT tag_name FROM rejected_suggestions WHERE image_id=$image");
        rejected.Parameters.AddWithValue("$image", imageId);
        using var rejectedReader = rejected.ExecuteReader();
        while (rejectedReader.Read()) tags.Remove(rejectedReader.GetString(0));
        return tags.Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public bool IsCurrent(ImageRecord image, ICloudVisionAnalyzer analyzer, string locationFingerprint)
    {
        using var command = database.Command("""
            SELECT 1 FROM cloud_vision_analyses
            WHERE image_id=$image AND provider=$provider AND model_version=$model
              AND prompt_version=$prompt AND source_file_size=$size
              AND source_modified_utc=$modified AND location_fingerprint=$location
              AND status='completed'
            """);
        AddIdentity(command, image, analyzer, locationFingerprint);
        return command.ExecuteScalar() is not null;
    }

    public void Queue(ImageRecord image, ICloudVisionAnalyzer analyzer, string locationFingerprint, string reason)
    {
        Upsert(image, analyzer, locationFingerprint, "queued", reason, null, null, null);
    }

    public void MarkProcessing(ImageRecord image, ICloudVisionAnalyzer analyzer, string locationFingerprint, string reason)
    {
        Upsert(image, analyzer, locationFingerprint, "processing", reason, null, null, null);
    }

    public void Save(ImageRecord image, ICloudVisionAnalyzer analyzer, string locationFingerprint,
        string? reason, CloudVisionResult result)
    {
        Upsert(image, analyzer, locationFingerprint, "completed", reason, result.Summary,
            JsonSerializer.Serialize(result), null);
    }

    public void SaveFailure(ImageRecord image, ICloudVisionAnalyzer analyzer, string locationFingerprint,
        string? reason, string errorCode)
    {
        Upsert(image, analyzer, locationFingerprint, "failed", reason, null, null, errorCode);
    }

    public (string Status, string? Summary, string? ResultJson)? Get(long imageId, string provider)
    {
        using var command = database.Command("SELECT status,summary,result_json FROM cloud_vision_analyses WHERE image_id=$image AND provider=$provider");
        command.Parameters.AddWithValue("$image", imageId);
        command.Parameters.AddWithValue("$provider", provider);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2))
            : null;
    }

    public (string Provider, string ModelVersion, string PromptVersion, CloudVisionResult Result)? GetLatestCompleted(long imageId)
    {
        using var command = database.Command("""
            SELECT provider,model_version,prompt_version,result_json
            FROM cloud_vision_analyses
            WHERE image_id=$image AND status='completed' AND result_json IS NOT NULL
            ORDER BY updated_utc DESC LIMIT 1
            """);
        command.Parameters.AddWithValue("$image", imageId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        try
        {
            var result = JsonSerializer.Deserialize<CloudVisionResult>(reader.GetString(3),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return result is null ? null : (reader.GetString(0), reader.GetString(1), reader.GetString(2), result);
        }
        catch (JsonException) { return null; }
    }

    private void Upsert(ImageRecord image, ICloudVisionAnalyzer analyzer, string locationFingerprint,
        string status, string? reason, string? summary, string? resultJson, string? errorCode)
    {
        using var command = database.Command("""
            INSERT INTO cloud_vision_analyses(image_id,provider,model_version,prompt_version,
              source_file_size,source_modified_utc,location_fingerprint,status,reason,summary,result_json,error_code,updated_utc,
              input_tokens,output_tokens,estimated_cost_yuan)
            VALUES($image,$provider,$model,$prompt,$size,$modified,$location,$status,$reason,$summary,$json,$error,$now,$input,$output,$cost)
            ON CONFLICT(image_id,provider) DO UPDATE SET model_version=excluded.model_version,
              prompt_version=excluded.prompt_version,source_file_size=excluded.source_file_size,
              source_modified_utc=excluded.source_modified_utc,location_fingerprint=excluded.location_fingerprint,
              status=excluded.status,reason=excluded.reason,summary=excluded.summary,result_json=excluded.result_json,
              error_code=excluded.error_code,updated_utc=excluded.updated_utc,input_tokens=excluded.input_tokens,
              output_tokens=excluded.output_tokens,estimated_cost_yuan=excluded.estimated_cost_yuan
            """);
        AddIdentity(command, image, analyzer, locationFingerprint);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$summary", (object?)summary ?? DBNull.Value);
        command.Parameters.AddWithValue("$json", (object?)resultJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        command.Parameters.AddWithValue("$input", resultJson is null ? 0 : TryUsage(resultJson)?.InputTokens ?? 0);
        command.Parameters.AddWithValue("$output", resultJson is null ? 0 : TryUsage(resultJson)?.OutputTokens ?? 0);
        command.Parameters.AddWithValue("$cost", resultJson is null ? 0m : TryUsage(resultJson)?.EstimatedCostYuan ?? 0m);
        command.ExecuteNonQuery();
    }

    private static CloudVisionUsage? TryUsage(string resultJson)
    {
        try { return JsonSerializer.Deserialize<CloudVisionResult>(resultJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.Usage; }
        catch (JsonException) { return null; }
    }

    private static void AddIdentity(Microsoft.Data.Sqlite.SqliteCommand command, ImageRecord image,
        ICloudVisionAnalyzer analyzer, string locationFingerprint)
    {
        command.Parameters.AddWithValue("$image", image.Id);
        command.Parameters.AddWithValue("$provider", analyzer.Provider);
        command.Parameters.AddWithValue("$model", analyzer.ModelVersion);
        command.Parameters.AddWithValue("$prompt", analyzer.PromptVersion);
        command.Parameters.AddWithValue("$size", image.FileSize);
        command.Parameters.AddWithValue("$modified", image.ModifiedUtc.ToUniversalTime().ToString("o"));
        command.Parameters.AddWithValue("$location", locationFingerprint);
    }
}
