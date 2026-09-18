using System.Text.Json;

namespace Picer.ModelServer;

/// <summary>Durable, append-only training memory. Raw embeddings are never written here.</summary>
public sealed class TrainingAttemptArchive
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public TrainingAttemptArchive()
    {
        var root = Environment.GetEnvironmentVariable("PICER_TRAINING_ARCHIVE_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "training-archive");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "attempts.jsonl");
    }

    public void Append(TrainingAttemptRecord record)
    {
        lock (_gate)
        {
            File.AppendAllText(_path, JsonSerializer.Serialize(record, _json) + Environment.NewLine);
        }
    }

    public IReadOnlyList<TrainingAttemptRecord> ReadRecent(int limit = 100)
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return [];
            return File.ReadLines(_path).Reverse().Take(Math.Clamp(limit, 1, 1000))
                .Select(line => JsonSerializer.Deserialize<TrainingAttemptRecord>(line, _json))
                .Where(item => item is not null).Cast<TrainingAttemptRecord>().ToArray();
        }
    }

    public string BuildSummary()
    {
        var records = ReadRecent(500);
        if (records.Count == 0) return "暂无历史训练记录";
        var completed = records.Where(r => r.Status == "completed" && r.ValidationAccuracy is not null).ToArray();
        var best = completed.OrderByDescending(r => r.ValidationAccuracy).FirstOrDefault();
        var failures = records.Count(r => r.Status == "failed");
        var attempts = records.Count;
        return $"历史尝试 {attempts} 次；失败 {failures} 次；" +
               (best is null ? "尚无可比较制品" : $"历史最佳准确率 {best.ValidationAccuracy:P2}、误识率 {best.FalseAcceptRate:P2}、拒识率 {best.FalseRejectRate:P2}、模型 {best.ModelVersion}");
    }

    public bool MarkApplied(string artifactId)
    {
        var record = ReadRecent(1000).FirstOrDefault(value =>
            string.Equals(value.ArtifactId, artifactId, StringComparison.Ordinal));
        if (record is null) return false;
        Append(record with { Status = "applied", Applied = true, CreatedUtc = DateTime.UtcNow });
        return true;
    }
}

public sealed record TrainingAttemptRecord(
    string DatasetId, string SnapshotId, string? JobId, string? ArtifactId,
    int Attempt, string ModelVersion, int SampleCount, int PersonCount,
    string Status, double? ValidationAccuracy, double? FalseAcceptRate,
    double? FalseRejectRate, string? AdvisorModel, string? AdvisorPromptVersion,
    string? TrainingHints, DateTime CreatedUtc, string? RecipeFingerprint = null,
    string? RecipeReason = null, int SelectedSampleCount = 0, int ExcludedSampleCount = 0,
    bool Applied = false, string? Scenario = null);
