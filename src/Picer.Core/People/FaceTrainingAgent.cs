using System.Security.Cryptography;
using System.Text;

namespace Picer.Core.People;

public sealed record FaceTrainingAgentResult(bool Passed, int Attempts, TrainingArtifactData? Artifact, string FailureReason);

/// <summary>统一的人脸训练 Agent：最多九轮，自适应配方，失败制品永不应用。</summary>
public sealed class FaceTrainingAgent(FaceTrainingSubmissionService submission)
{
    public async Task<FaceTrainingAgentResult> RunAsync(FaceTrainingSnapshotResult snapshot,
        IReadOnlyList<(long FaceId, long PersonId, float[] Embedding, string Split)> samples,
        Func<int, TrainingArtifactData, CancellationToken, Task<string?>>? advisor = null,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<long, TrainingVisualAttribute>? visualAttributes = null,
        IReadOnlyList<TrainingActionEvidence>? actions = null, string? initialHints = null)
    {
        const double gate = 0.90;
        const int maxAttempts = 9;
        const string corePrinciples = "核心训练准则：按时间跨度分层抽样，保证同一人物覆盖不同年份/季节/设备；同一原图、连拍组和视频关键帧不得跨分区；连拍或内容哈希高度相似的人脸先去重，避免单一时刻过度代表；优先保留跨时间稳定特征，并用时间变化样本检验泛化能力；先判断阈值可达性，若扫描仍低于90%则停止低频调阈值并升级基模。";
        string? hints = await submission.GetTrainingMemoryAsync(cancellationToken);
        hints = string.IsNullOrWhiteSpace(hints) ? corePrinciples : $"{corePrinciples}\n历史训练记忆：{hints}";
        if (!string.IsNullOrWhiteSpace(initialHints))
            hints = string.IsNullOrWhiteSpace(hints) ? initialHints : $"{hints}\n用户操作摘要：{initialHints}";
        TrainingArtifactData? last = null;
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var recipe = BuildRecipe(samples, attempt, visualAttributes, actions);
            if (!fingerprints.Add(recipe.Fingerprint))
            {
                progress?.Report($"训练 Agent：第 {attempt}/{maxAttempts} 次，数据配方未变化，跳过重复提交（{recipe.Fingerprint}）");
                return new(false, Math.Max(0, attempt - 1), last,
                    "下一轮数据配方与上一轮相同，已跳过重复训练；请补充不同姿态、光照或排他负样本");
            }
            var recipeHints = $"数据配方={recipe.Fingerprint}；选中={recipe.Samples.Count}；排除={recipe.ExcludedCount}；原因={recipe.Reason}";
            var roundHints = string.IsNullOrWhiteSpace(hints) ? recipeHints : $"{hints}\n{recipeHints}";
            progress?.Report($"训练 Agent：第 {attempt}/{maxAttempts} 次：{recipe.Reason}；选中 {recipe.Samples.Count}，排除 {recipe.ExcludedCount}，指纹 {recipe.Fingerprint}");
            var submitted = await submission.SubmitAsync(snapshot, recipe.Samples, cancellationToken, attempt, roundHints,
                visualAttributes, actions, recipe.Fingerprint, recipe.Reason, recipe.ExcludedCount);
            if (submitted.Status == "failed")
            {
                if (attempt == maxAttempts) return new(false, attempt, null, submitted.Error ?? "训练提交失败");
                continue;
            }
            TrainingArtifactData? artifact = null;
            for (var poll = 0; poll < 120; poll++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(1000, cancellationToken);
                var status = await submission.PollAsync(submitted.JobId, cancellationToken);
                progress?.Report($"训练 Agent：第 {attempt}/{maxAttempts} 次，{status.Stage ?? status.Status} {status.Progress}%");
                if (status.Status is "failed" or "canceled") break;
                if (status.Status == "completed" && status.ArtifactId is not null)
                {
                    artifact = await submission.GetArtifactAsync(status.ArtifactId, cancellationToken);
                    break;
                }
            }
            if (artifact is null)
            {
                if (attempt == maxAttempts) return new(false, attempt, null, "训练任务未生成制品");
                continue;
            }
            last = artifact;
            if (!await submission.VerifyArtifactAsync(artifact, cancellationToken))
            {
                if (attempt == maxAttempts) return new(false, attempt, artifact, "制品签名校验失败");
                continue;
            }
            if (string.Equals(artifact.Diagnostics?.RecommendedAction, "upgrade-model", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report($"训练 Agent：第 {attempt} 次诊断为基模能力不足，停止重复调阈值：{artifact.Diagnostics?.Explanation}");
                return new(false, attempt, artifact, "阈值扫描仍无法达到 90%，应切换或并行评测更强人脸基模");
            }
            if (artifact.Metrics.ValidationAccuracy >= gate)
                return new(true, attempt, artifact, "准确率门禁通过");
            var advice = advisor is null ? "提高困难负样本覆盖并重新评测" : await advisor(attempt, artifact, cancellationToken);
            hints = string.IsNullOrWhiteSpace(roundHints) ? advice : $"{roundHints}\n本轮调整建议：{advice}";
            progress?.Report($"训练 Agent：第 {attempt} 次未达 90%，已生成下一轮配方调整建议");
        }
        return new(false, maxAttempts, last, "连续 9 次未达到准确率 90%");
    }

    private static AttemptRecipe BuildRecipe(
        IReadOnlyList<(long FaceId, long PersonId, float[] Embedding, string Split)> source,
        int attempt, IReadOnlyDictionary<long, TrainingVisualAttribute>? attributes,
        IReadOnlyList<TrainingActionEvidence>? actions)
    {
        var errorFaces = actions?.Where(a => a.Action is "reject" or "move_out" or "reclassify" or "wrong")
            .Select(a => a.FaceId).ToHashSet() ?? [];
        var fraction = attempt switch { 1 => 1d, 2 => .90d, 3 => .80d, 4 => .85d, _ => .90d };
        var selected = source.GroupBy(s => s.PersonId).SelectMany(group =>
        {
            var ordered = group.OrderByDescending(s => errorFaces.Contains(s.FaceId))
                .ThenByDescending(s => Difficulty(s, attributes, attempt))
                .ThenBy(s => Stable(attempt, s.FaceId)).ToArray();
            var count = Math.Min(ordered.Length, Math.Max(2, (int)Math.Ceiling(ordered.Length * fraction)));
            var deduplicated = ordered.GroupBy(s =>
            {
                if (attributes?.TryGetValue(s.FaceId, out var value) == true)
                    return value.BurstGroup ?? value.ContentHash ?? $"face:{s.FaceId}";
                return $"face:{s.FaceId}";
            }, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
            var chosen = new List<(long FaceId, long PersonId, float[] Embedding, string Split)>();
            // If capture dates are available, reserve one representative from
            // each year before filling the remaining quota. This prevents a
            // burst from dominating a person and tests time-shifted features.
            var dated = deduplicated.Where(s => attributes?.TryGetValue(s.FaceId, out var value) == true && value.CaptureUtc is not null)
                .GroupBy(s => attributes![s.FaceId].CaptureUtc!.Value.Year).Select(g => g.First());
            chosen.AddRange(dated.Take(count));
            chosen.AddRange(deduplicated.Where(s => !chosen.Contains(s)).Take(Math.Max(0, count - chosen.Count)));
            foreach (var item in deduplicated.Where(s => s.Split != "train" && !chosen.Contains(s)).Take(1)) chosen.Add(item);
            return chosen;
        }).DistinctBy(s => s.FaceId).OrderBy(s => s.FaceId).ToArray();
        var fingerprintText = string.Join('|', selected.Select(s => $"{s.FaceId}:{s.PersonId}:{s.Split}"));
        var fingerprint = "recipe-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintText)))[..16].ToLowerInvariant();
        var reason = attempt == 1 ? "基线配方：保留全部人工确认样本" :
            errorFaces.Count > 0 ? "优先纳入用户纠错样本，并按姿态/光照/遮挡质量重新配比" :
            "按 VL 质量与困难姿态重新配比，减少冗余样本";
        return new(selected, source.Count - selected.Length, fingerprint, reason);
    }

    private static double Difficulty((long FaceId, long PersonId, float[] Embedding, string Split) sample,
        IReadOnlyDictionary<long, TrainingVisualAttribute>? attributes, int attempt)
    {
        if (attributes is null || !attributes.TryGetValue(sample.FaceId, out var value))
            return Stable(attempt, sample.FaceId) / (double)uint.MaxValue;
        var score = 1d - value.FaceQuality;
        if (!value.Pose.Equals("front", StringComparison.OrdinalIgnoreCase)) score += .25;
        if (!value.Lighting.Equals("good", StringComparison.OrdinalIgnoreCase)) score += .2;
        score += Math.Min(.3, value.Occlusion.Count * .1);
        return score;
    }

    private static uint Stable(int seed, long id) =>
        BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{id}")), 0);

    private sealed record AttemptRecipe(
        IReadOnlyList<(long FaceId, long PersonId, float[] Embedding, string Split)> Samples,
        int ExcludedCount, string Fingerprint, string Reason);
}
