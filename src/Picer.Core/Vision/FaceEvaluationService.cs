using System.Text.Json;

namespace Picer.Core.Vision;

public sealed record FaceEvaluationPolicy(string Version, IReadOnlyDictionary<long, float[]> Prototypes,
    IReadOnlyDictionary<long, double> Thresholds, double DefaultThreshold = .62);

public sealed record FaceMetric(string Scope, string ScopeKey, int TruePositive, int FalsePositive,
    int FalseNegative, int TrueNegative, int Total,
    double Precision, double Recall, double Far, double Frr, double UnknownRejectionRate);

public sealed record FaceEvaluationObservation(long PersonId, bool Positive, bool Accepted,
    string SampleKey, double Quality);

public sealed record FaceEvaluationResult(string RunId, string SnapshotId, string StrategyVersion,
    string Partition, FaceMetric Overall, IReadOnlyList<FaceMetric> PerPerson,
    IReadOnlyList<FaceMetric> DifficultyGroups, bool Passed, IReadOnlyList<string> BlockingReasons);

public sealed record FaceEvaluationComparison(FaceEvaluationResult Baseline, FaceEvaluationResult Candidate,
    bool Passed, IReadOnlyList<string> BlockingReasons);

/// <summary>Shadow evaluator for FACE-EVAL-002. It never changes face assignments or policies.</summary>
public sealed class FaceEvaluationService(VisionAnalysisRepository repository)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public FaceEvaluationResult Evaluate(string snapshotId, FaceEvaluationPolicy policy,
        string partition = "test", IReadOnlyDictionary<string, IReadOnlyList<string>>? difficultyGroups = null,
        bool persist = true)
    {
        var snapshot = new FaceEvaluationSnapshotService(repository).Get(snapshotId)
            ?? throw new InvalidOperationException($"评测快照不存在: {snapshotId}");
        var members = new FaceEvaluationSnapshotService(repository).GetMembers(snapshotId)
            .Where(member => member.Partition == partition).ToArray();
        if (members.Length == 0) throw new InvalidOperationException($"评测分区为空: {partition}");
        var rows = new List<FaceEvaluationObservation>();
        foreach (var member in members)
        {
            if (!policy.Prototypes.TryGetValue(member.PersonId, out var prototype)) continue;
            var threshold = policy.Thresholds.GetValueOrDefault(member.PersonId, policy.DefaultThreshold);
            var score = Cosine(member.Embedding, prototype);
            rows.Add(new(member.PersonId, member.Label == "positive", score >= threshold, member.SampleKey, member.QualityScore));
        }
        if (rows.Count == 0) throw new InvalidOperationException("评测分区没有可用的人物原型。");
        var perPerson = rows.GroupBy(row => row.PersonId).Select(group => CalculateMetric("person", group.Key.ToString(), group)).ToArray();
        var overall = CalculateMetric("overall", "all", rows);
        var grouped = new Dictionary<string, List<FaceEvaluationObservation>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var labels = difficultyGroups?.GetValueOrDefault(row.SampleKey) ?? InferDifficulty(row.Quality);
            foreach (var label in labels)
            {
                if (!grouped.TryGetValue(label, out var values)) grouped[label] = values = [];
                values.Add(row);
            }
        }
        var difficulties = grouped.Select(group => CalculateMetric("difficulty", group.Key, group.Value)).ToArray();
        var reasons = new List<string>();
        if (overall.Far > .01) reasons.Add($"FAR {overall.Far:P2} 超过 1% 门限");
        if (overall.UnknownRejectionRate < .90 && overall.TrueNegative + overall.FalsePositive > 0) reasons.Add($"未知人物拒识率仅 {overall.UnknownRejectionRate:P2}");
        var passed = reasons.Count == 0;
        var runId = "face-eval-" + Guid.NewGuid().ToString("N");
        if (persist) Persist(runId, snapshot, policy.Version, partition, passed, overall, perPerson, difficulties);
        return new(runId, snapshotId, policy.Version, partition, overall, perPerson, difficulties, passed, reasons);
    }

    public FaceEvaluationComparison Compare(FaceEvaluationResult baseline, FaceEvaluationResult candidate,
        double maxFrrIncrease = .02, double maxRecallDrop = .01)
    {
        var reasons = new List<string>();
        if (candidate.Overall.Far > baseline.Overall.Far + 1e-12) reasons.Add("候选策略 FAR 劣化");
        if (candidate.Overall.Frr > baseline.Overall.Frr + maxFrrIncrease) reasons.Add("候选策略 FRR 劣化超过门限");
        if (candidate.Overall.Recall < baseline.Overall.Recall - maxRecallDrop) reasons.Add("候选策略 Recall 劣化超过门限");
        if (candidate.Overall.UnknownRejectionRate < baseline.Overall.UnknownRejectionRate - maxRecallDrop) reasons.Add("候选策略未知人物拒识率劣化");
        var oldGroups = baseline.DifficultyGroups.ToDictionary(metric => metric.ScopeKey, StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidate.DifficultyGroups)
            if (oldGroups.TryGetValue(group.ScopeKey, out var old) && (group.Far > old.Far + 1e-12 || group.Recall < old.Recall - maxRecallDrop))
                reasons.Add($"困难分组 {group.ScopeKey} 劣化");
        return new(baseline, candidate, reasons.Count == 0, reasons);
    }

    public static FaceEvaluationPolicy BuildPolicy(string version, IReadOnlyList<FaceEvaluationSnapshotMember> members,
        double threshold = .62)
    {
        var prototypes = members.Where(member => member.Partition == "train" && member.Label == "positive")
            .GroupBy(member => member.PersonId).ToDictionary(group => group.Key, group => Mean(group.Select(member => member.Embedding)));
        return new(version, prototypes, prototypes.Keys.ToDictionary(id => id, _ => threshold), threshold);
    }

    private void Persist(string runId, FaceEvaluationSnapshot snapshot, string strategy, string partition, bool passed,
        FaceMetric overall, IReadOnlyList<FaceMetric> people, IReadOnlyList<FaceMetric> difficulties)
    {
        using var tx = repository.Database.BeginTransaction();
        using (var command = repository.Database.Command("INSERT INTO face_evaluation_runs(run_id,snapshot_id,strategy_version,partition,status,passed,created_utc,completed_utc) VALUES($id,$snapshot,$strategy,$partition,'completed',$passed,$now,$now)"))
        {
            command.Transaction = tx; command.Parameters.AddWithValue("$id", runId); command.Parameters.AddWithValue("$snapshot", snapshot.SnapshotId); command.Parameters.AddWithValue("$strategy", strategy); command.Parameters.AddWithValue("$partition", partition); command.Parameters.AddWithValue("$passed", passed ? 1 : 0); command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o")); command.ExecuteNonQuery();
        }
        foreach (var metric in new[] { overall }.Concat(people).Concat(difficulties))
        {
            using var command = repository.Database.Command("INSERT INTO face_evaluation_metrics(run_id,scope,scope_key,metrics_json) VALUES($run,$scope,$key,$json)");
            command.Transaction = tx; command.Parameters.AddWithValue("$run", runId); command.Parameters.AddWithValue("$scope", metric.Scope); command.Parameters.AddWithValue("$key", metric.ScopeKey); command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(metric, Json)); command.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public static FaceMetric CalculateMetric(string scope, string key, IEnumerable<FaceEvaluationObservation> rows)
    {
        var values = rows.ToArray(); var tp = values.Count(row => row.Positive && row.Accepted); var fn = values.Count(row => row.Positive && !row.Accepted); var fp = values.Count(row => !row.Positive && row.Accepted); var tn = values.Count(row => !row.Positive && !row.Accepted);
        return new(scope, key, tp, fp, fn, tn, values.Length, Rate(tp, tp + fp), Rate(tp, tp + fn), Rate(fp, fp + tn), Rate(fn, fn + tp), Rate(tn, tn + fp));
    }

    private static IReadOnlyList<string> InferDifficulty(double quality) => quality < .4 ? ["small-face", "low-confidence"] : quality < .6 ? ["low-confidence"] : ["standard"];
    private static double Rate(int numerator, int denominator) => denominator == 0 ? 0 : numerator / (double)denominator;
    private static float[] Mean(IEnumerable<float[]> vectors) { var values = vectors.ToArray(); var result = new float[values[0].Length]; foreach (var vector in values) for (var i = 0; i < result.Length; i++) result[i] += vector[i]; var norm = Math.Sqrt(result.Sum(value => value * value)); if (norm > 0) for (var i = 0; i < result.Length; i++) result[i] /= (float)norm; return result; }
    private static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right) { if (left.Count != right.Count) return -1; double dot = 0, a = 0, b = 0; for (var i = 0; i < left.Count; i++) { dot += left[i] * right[i]; a += left[i] * left[i]; b += right[i] * right[i]; } return a <= 0 || b <= 0 ? -1 : dot / Math.Sqrt(a * b); }
}
