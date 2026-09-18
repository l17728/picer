using System.Security.Cryptography;
using System.Text;

namespace Picer.Core.People;

public sealed record FaceTrainingAgentResult(bool Passed, int Attempts, TrainingArtifactData? Artifact, string FailureReason,
    IReadOnlyList<TrainingArtifactData>? PassedArtifacts = null, string? ExperienceSummary = null);
public sealed record FaceTrainingModelResult(string ModelVersion, FaceTrainingAgentResult Result);

/// <summary>统一的人脸训练 Agent：最多九轮，自适应配方，失败制品永不应用。</summary>
public sealed class FaceTrainingAgent(FaceTrainingSubmissionService submission)
{
    public async Task<FaceTrainingAgentResult> RunAsync(FaceTrainingSnapshotResult snapshot,
        IReadOnlyList<(long FaceId, long PersonId, float[] Embedding, string Split)> samples,
        Func<int, TrainingArtifactData, CancellationToken, Task<string?>>? advisor = null,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<long, TrainingVisualAttribute>? visualAttributes = null,
        IReadOnlyList<TrainingActionEvidence>? actions = null, string? initialHints = null,
        string modelVersion = "sface")
    {
        const double gate = 0.90;
        const int maxAttempts = 9;
        const string corePrinciples = "核心训练准则：按时间跨度分层抽样，保证同一人物覆盖不同年份/季节/设备；同一原图、连拍组和视频关键帧不得跨分区；连拍或内容哈希高度相似的人脸先去重，避免单一时刻过度代表；优先保留跨时间稳定特征，并用时间变化样本检验泛化能力；先判断阈值可达性，若扫描仍低于90%则停止低频调阈值并升级基模。";
        string? hints = await submission.GetTrainingMemoryAsync(modelVersion, cancellationToken);
        hints = string.IsNullOrWhiteSpace(hints) ? corePrinciples : $"{corePrinciples}\n历史训练记忆：{hints}";
        var actionEvidence = BuildActionEvidenceSummary(actions, modelVersion);
        if (!string.IsNullOrWhiteSpace(actionEvidence))
            hints = $"{hints}\n人工纠错困难样本证据：{actionEvidence}";
        if (!string.IsNullOrWhiteSpace(initialHints))
            hints = string.IsNullOrWhiteSpace(hints) ? initialHints : $"{hints}\n用户操作摘要：{initialHints}";
        TrainingArtifactData? last = null;
        TrainingArtifactData? best = null;
        var passedArtifacts = new List<TrainingArtifactData>();
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var recipe = BuildRecipe(samples, attempt, visualAttributes, actions);
            // The server history is part of the Agent memory. Avoid replaying a
            // recipe that was already attempted in an earlier run, not only in
            // this process.
            if ((hints ?? string.Empty).Contains(recipe.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report($"训练 Agent：历史已存在配方 {recipe.Fingerprint}，跳过重复提交");
                continue;
            }
            if (!fingerprints.Add(recipe.Fingerprint))
            {
                progress?.Report($"训练 Agent：第 {attempt}/{maxAttempts} 次，数据配方未变化，跳过重复提交（{recipe.Fingerprint}）");
                continue;
            }
            var recipeHints = $"数据配方={recipe.Fingerprint}；选中={recipe.Samples.Count}；排除={recipe.ExcludedCount}；原因={recipe.Reason}";
            var roundHints = string.IsNullOrWhiteSpace(hints) ? recipeHints : $"{hints}\n{recipeHints}";
            progress?.Report($"训练 Agent：第 {attempt}/{maxAttempts} 次：{recipe.Reason}；选中 {recipe.Samples.Count}，排除 {recipe.ExcludedCount}，指纹 {recipe.Fingerprint}");
            var submitted = await submission.SubmitAsync(snapshot, recipe.Samples, cancellationToken, attempt, roundHints,
                visualAttributes, actions, recipe.Fingerprint, recipe.Reason, recipe.ExcludedCount, modelVersion);
            if (submitted.Status == "failed")
            {
                if (attempt == maxAttempts) return new(passedArtifacts.Count > 0, attempt, best,
                    submitted.Error ?? "训练提交失败", passedArtifacts, BuildExperienceSummary(passedArtifacts));
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
                if (attempt == maxAttempts) return new(passedArtifacts.Count > 0, attempt, best,
                    "训练任务未生成制品", passedArtifacts, BuildExperienceSummary(passedArtifacts));
                continue;
            }
            last = artifact;
            if (!await submission.VerifyArtifactAsync(artifact, cancellationToken))
            {
                if (attempt == maxAttempts) return new(passedArtifacts.Count > 0, attempt, best ?? artifact,
                    "制品签名校验失败", passedArtifacts, BuildExperienceSummary(passedArtifacts));
                continue;
            }
            if (string.Equals(artifact.Diagnostics?.RecommendedAction, "upgrade-model", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report($"训练 Agent：第 {attempt} 次诊断为基模能力不足，停止重复调阈值：{artifact.Diagnostics?.Explanation}");
                hints = $"{roundHints}\n基模诊断：{artifact.Diagnostics?.Explanation}；仍按下一轮配方继续验证，避免单轮误判。";
                continue;
            }
            if (artifact.Metrics.ValidationAccuracy >= gate)
            {
                passedArtifacts.Add(artifact);
                if (best is null || artifact.Metrics.ValidationAccuracy > best.Metrics.ValidationAccuracy)
                    best = artifact;
                hints = $"{roundHints}\n通过轮次经验：本轮 {artifact.Metrics.ValidationAccuracy:P1}；截至目前最高 {best.Metrics.ValidationAccuracy:P1}；继续优化人物间样本平衡、时间跨度、姿态/光照覆盖并去除连拍重复。";
                progress?.Report($"训练 Agent：第 {attempt}/{maxAttempts} 次达到 90%，继续优化；当前最高 {best.Metrics.ValidationAccuracy:P1}");
                continue;
            }
            var advice = advisor is null ? "提高困难负样本覆盖并重新评测" : await advisor(attempt, artifact, cancellationToken);
            hints = string.IsNullOrWhiteSpace(roundHints) ? advice : $"{roundHints}\n本轮调整建议：{advice}";
            progress?.Report($"训练 Agent：第 {attempt} 次未达 90%，已生成下一轮配方调整建议");
        }
        return new(passedArtifacts.Count > 0, maxAttempts, best ?? last,
            passedArtifacts.Count > 0 ? "9 轮迭代完成，已保留所有达到 90% 的制品" : "连续 9 次未达到准确率 90%",
            passedArtifacts, BuildExperienceSummary(passedArtifacts));
    }

    private static string? BuildExperienceSummary(IReadOnlyList<TrainingArtifactData> artifacts)
    {
        if (artifacts.Count == 0) return null;
        var best = artifacts.OrderByDescending(value => value.Metrics.ValidationAccuracy).First();
        var scores = string.Join("、", artifacts.Select(value => value.Metrics.ValidationAccuracy.ToString("P1")));
        return $"通过制品经验：共 {artifacts.Count} 个制品达到 90% 门禁；准确率序列 {scores}；最高 {best.Metrics.ValidationAccuracy:P1}（{best.ArtifactId}）。后续优先参考最高分配方，并保留其困难样本、时间跨度和去重策略。";
    }

    /// <summary>每个模型独立运行九轮 Agent；配方、历史记忆和失败不会互相污染。</summary>
    public async Task<IReadOnlyList<FaceTrainingModelResult>> RunParallelAsync(
        FaceTrainingSnapshotResult snapshot,
        IReadOnlyDictionary<string, IReadOnlyList<(long FaceId, long PersonId, float[] Embedding, string Split)>> modelSamples,
        Func<string, int, TrainingArtifactData, CancellationToken, Task<string?>>? advisor = null,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<long, TrainingVisualAttribute>? visualAttributes = null,
        IReadOnlyList<TrainingActionEvidence>? actions = null)
    {
        var tasks = modelSamples.Where(pair => pair.Value.Count > 0).Select(async pair =>
        {
            var result = await RunAsync(snapshot, pair.Value,
                advisor is null ? null : (attempt, artifact, token) => advisor(pair.Key, attempt, artifact, token),
                progress, cancellationToken, visualAttributes, actions, modelVersion: pair.Key).ConfigureAwait(false);
            return new FaceTrainingModelResult(pair.Key, result);
        });
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static AttemptRecipe BuildRecipe(
        IReadOnlyList<(long FaceId, long PersonId, float[] Embedding, string Split)> source,
        int attempt, IReadOnlyDictionary<long, TrainingVisualAttribute>? attributes,
        IReadOnlyList<TrainingActionEvidence>? actions)
    {
        source = source.Where(s => s.Split is "train" or "validation").ToArray();
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
            return chosen;
        }).DistinctBy(s => s.FaceId).OrderBy(s => s.FaceId).ToArray();
        var fingerprintText = string.Join('|', selected.Select(s => $"{s.FaceId}:{s.PersonId}:{s.Split}"));
        var fingerprint = "recipe-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintText)))[..16].ToLowerInvariant();
        var reason = attempt == 1 ? "基线配方：保留全部人工确认样本" :
            errorFaces.Count > 0 ? "优先纳入用户纠错样本，并按姿态/光照/遮挡质量重新配比" :
            "按 VL 质量与困难姿态重新配比，减少冗余样本";
        return new(selected, source.Count - selected.Length, fingerprint, reason);
    }

    private static string BuildActionEvidenceSummary(IReadOnlyList<TrainingActionEvidence>? actions, string modelVersion)
    {
        if (actions is null) return string.Empty;
        var relevant = actions.Where(action => string.Equals(action.ModelVersion, modelVersion, StringComparison.OrdinalIgnoreCase)
            && action.Action is "reject" or "reclassify" or "move_out" or "ignore" or "wrong").ToArray();
        if (relevant.Length == 0) return string.Empty;
        var pairs = relevant.Where(action => action.SourcePersonId is not null)
            .GroupBy(action => (Predicted: action.SourcePersonId!.Value, Actual: action.PersonId ?? 0))
            .OrderByDescending(group => group.Count())
            .Take(20)
            .Select(group => $"预测人物{group.Key.Predicted}→实际{group.Key.Actual} {group.Count()}条（作为预测人物排他负样本、实际人物困难正样本）");
        var ignored = relevant.Count(action => action.Action == "ignore");
        var rejected = relevant.Count(action => action.Action is "reject" or "wrong");
        return string.Join("；", pairs.Concat([
            $"纠错总数={relevant.Length}",
            $"拒绝/错误={rejected}",
            $"不分类={ignored}（作为未知人物拒识样本，不归入任何人物）"
        ]));
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
