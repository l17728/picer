using System.Text.Json;

namespace Picer.Core.Vision;

/// <summary>Algorithm-neutral input for a cross-backbone evaluation run.</summary>
public sealed record FaceAlgorithmEvaluationSpec(string AlgorithmId, string EmbeddingModelVersion,
    string SnapshotId, FaceEvaluationPolicy Policy);

public sealed record FaceAlgorithmEvaluationEntry(string AlgorithmId, string EmbeddingModelVersion,
    string SnapshotId, FaceEvaluationResult Result);

public sealed record FaceAlgorithmBenchmarkResult(DateTime GeneratedUtc, string BenchmarkVersion,
    IReadOnlyList<FaceAlgorithmEvaluationEntry> Entries, string? RecommendedAlgorithmId)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
}

/// <summary>Runs the same evaluator against independently frozen snapshots for different algorithms.</summary>
public sealed class FaceAlgorithmBenchmarkService(VisionAnalysisRepository repository)
{
    private readonly FaceEvaluationService _evaluator = new(repository);

    public FaceAlgorithmBenchmarkResult Evaluate(IReadOnlyList<FaceAlgorithmEvaluationSpec> specifications,
        string partition = "test")
    {
        if (specifications.Count == 0) throw new ArgumentException("至少需要一个算法评测规格。", nameof(specifications));
        var duplicate = specifications.GroupBy(x => x.AlgorithmId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"算法 {duplicate.Key} 重复，必须使用独立算法标识。", nameof(specifications));
        var entries = specifications.Select(spec =>
        {
            if (string.IsNullOrWhiteSpace(spec.AlgorithmId) || string.IsNullOrWhiteSpace(spec.EmbeddingModelVersion))
                throw new ArgumentException("算法标识和向量模型版本不能为空。", nameof(specifications));
            var result = _evaluator.Evaluate(spec.SnapshotId, spec.Policy, partition);
            return new FaceAlgorithmEvaluationEntry(spec.AlgorithmId, spec.EmbeddingModelVersion, spec.SnapshotId, result);
        }).ToArray();
        return new(DateTime.UtcNow, "face-algorithm-benchmark-v1", entries, Recommend(entries));
    }

    public static string? Recommend(IEnumerable<FaceAlgorithmEvaluationEntry> entries) => entries
        .Where(x => x.Result.Passed)
        .OrderBy(x => x.Result.Overall.Far)
        .ThenByDescending(x => x.Result.Overall.Recall)
        .ThenByDescending(x => x.Result.Overall.Precision)
        .Select(x => x.AlgorithmId).FirstOrDefault();
}
