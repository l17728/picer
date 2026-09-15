namespace Picer.Core.People;

public sealed record FaceTrainingAgentResult(bool Passed, int Attempts, TrainingArtifactData? Artifact, string FailureReason);

/// <summary>Runs bounded, deterministic training attempts and never applies a failing artifact.</summary>
public sealed class FaceTrainingAgent(FaceTrainingSubmissionService submission)
{
    public async Task<FaceTrainingAgentResult> RunAsync(FaceTrainingSnapshotResult snapshot,
        IReadOnlyList<(long FaceId, long PersonId, float[] Embedding, string Split)> samples,
        Func<int, TrainingArtifactData, CancellationToken, Task<string?>>? advisor = null,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<long, TrainingVisualAttribute>? visualAttributes = null,
        IReadOnlyList<TrainingActionEvidence>? actions = null)
    {
        const double gate = 0.90;
        const int maxAttempts = 9;
        string? hints = await submission.GetTrainingMemoryAsync(cancellationToken);
        TrainingArtifactData? last = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            progress?.Report($"训练 Agent：第 {attempt}/{maxAttempts} 次提交");
            var submitted = await submission.SubmitAsync(snapshot, samples, cancellationToken, attempt, hints, visualAttributes, actions);
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
            if (artifact.Metrics.ValidationAccuracy >= gate)
                return new(true, attempt, artifact, "准确率门禁通过");
            var advice = advisor is null ? "提高困难负样本覆盖并重新评测" : await advisor(attempt, artifact, cancellationToken);
            hints = string.IsNullOrWhiteSpace(hints) ? advice : $"{hints}\n本轮调整建议：{advice}";
            progress?.Report($"训练 Agent：第 {attempt} 次未达 90%，已生成下一次调整建议");
        }
        return new(false, maxAttempts, last, $"连续 {maxAttempts} 次未达到准确率 90%");
    }
}
