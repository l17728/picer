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
        EnsureColdStartKnowledge();
    }

    private void EnsureColdStartKnowledge()
    {
        if (ReadRecent(1000).Any(record => string.Equals(record.Status, "knowledge", StringComparison.OrdinalIgnoreCase)
            && string.Equals(record.RecipeReason, "cold-start-insight-history", StringComparison.OrdinalIgnoreCase))) return;
        Append(new TrainingAttemptRecord("cold-start-knowledge", "", null, null, 0,
            "insightface-arcface", 575, 6, "knowledge", 0.9302, null, null,
            "training-agent-memory", "face-training-memory.v1",
            "冷启动经验：历史 InsightFace 两轮有效训练分别为 91.7%（378 个样本）和 93.0%（197 个样本）。两轮均采用人工确认正样本的基线配方；后续应优先保持人工确认样本，按人物和时间跨度分层，连拍/同图/视频关键帧防泄漏并去重，同时保留跨年份、姿态、光照和设备变化。93.0% 是历史最高参考，不得用低于该分数的新制品覆盖；每轮应记录样本指纹、排除原因、困难负样本和最终指标。当前两件原制品已丢失，因此该记录仅作为冷启动经验，不代表可直接应用的制品。",
            DateTime.UtcNow, "cold-start-insight-2026", "cold-start-insight-history", 575, 0, false, "cold-start"));
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
