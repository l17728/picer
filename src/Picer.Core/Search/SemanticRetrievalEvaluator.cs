using System.Text.Json;

namespace Picer.Core.Search;

public sealed record RetrievalEvaluationCase(string Query, IReadOnlyList<long> RelevantIds,
    string? Group = null);

public sealed record RetrievalEvaluationMetrics(int Cases, double CandidateRecall, double FinalPrecision,
    int FailedRecallCases, int FailedPrecisionCases);

public sealed record RetrievalEvaluationReport(IReadOnlyList<RetrievalEvaluationCase> Cases,
    RetrievalEvaluationMetrics Metrics, DateTime EvaluatedUtc, string EvaluatorVersion);

/// <summary>Deterministic offline gate for the versioned semantic retrieval acceptance set.</summary>
public static class SemanticRetrievalEvaluator
{
    public static RetrievalEvaluationReport Evaluate(
        IReadOnlyList<RetrievalEvaluationCase> cases,
        Func<string, (IReadOnlyCollection<long> Candidates, IReadOnlyCollection<long> Final)> retrieve,
        string evaluatorVersion = "qry-eval-v1")
    {
        ArgumentNullException.ThrowIfNull(cases);
        var recallSum = 0d; var precisionSum = 0d; var recallFailures = 0; var precisionFailures = 0;
        foreach (var item in cases)
        {
            var expected = item.RelevantIds.Where(id => id > 0).ToHashSet();
            var result = retrieve(item.Query);
            var candidates = result.Candidates.ToHashSet();
            var final = result.Final.ToHashSet();
            var recall = expected.Count == 0 ? 1d : expected.Count(id => candidates.Contains(id)) / (double)expected.Count;
            var precision = final.Count == 0 ? 1d : final.Count(id => expected.Contains(id)) / (double)final.Count;
            recallSum += recall; precisionSum += precision;
            if (recall < 1d) recallFailures++;
            if (precision < .9d) precisionFailures++;
        }
        var metrics = new RetrievalEvaluationMetrics(cases.Count,
            cases.Count == 0 ? 1d : recallSum / cases.Count,
            cases.Count == 0 ? 1d : precisionSum / cases.Count,
            recallFailures, precisionFailures);
        return new RetrievalEvaluationReport(cases, metrics, DateTime.UtcNow, evaluatorVersion);
    }

    public static void SaveJson(string path, RetrievalEvaluationReport report)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static RetrievalEvaluationReport LoadJson(string path) =>
        JsonSerializer.Deserialize<RetrievalEvaluationReport>(File.ReadAllText(path))
        ?? throw new InvalidDataException("检索验收集为空。");

    public static IReadOnlyList<RetrievalEvaluationCase> LoadCasesJson(string path)
    {
        var cases = JsonSerializer.Deserialize<List<RetrievalEvaluationCase>>(File.ReadAllText(path));
        return cases is { Count: > 0 } ? cases : throw new InvalidDataException("检索验收集为空。");
    }

    public static void AssertReleaseGate(RetrievalEvaluationMetrics metrics)
    {
        if (metrics.CandidateRecall < 1d || metrics.FailedRecallCases > 0)
            throw new InvalidOperationException($"候选召回未通过发布门禁：{metrics.CandidateRecall:P1}");
        if (metrics.FinalPrecision < .9d || metrics.FailedPrecisionCases > 0)
            throw new InvalidOperationException($"最终精确率未通过发布门禁：{metrics.FinalPrecision:P1}");
    }
}
