namespace Picer.Core.Vision;

public sealed record FaceTrainingReadiness(bool Ready, int People, int PositiveSamples, int NegativeSamples,
    int LeakageGroups, int ValidationSamples, int TestSamples, IReadOnlyList<string> Warnings);

/// <summary>Validates training data volume and independent evaluation coverage before submission.</summary>
public sealed class FaceTrainingReadinessService(VisionAnalysisRepository repository)
{
    public FaceTrainingReadiness Check(string snapshotId, int minimumPositivePerPerson = 10, int minimumNegative = 10)
    {
        var snapshot = new FaceEvaluationSnapshotService(repository).Get(snapshotId)
            ?? throw new InvalidOperationException($"训练快照不存在：{snapshotId}");
        var members = new FaceEvaluationSnapshotService(repository).GetMembers(snapshotId);
        var warnings = new List<string>();
        var positives = members.Where(x => x.Label == "positive").ToArray();
        var negatives = members.Where(x => x.Label == "negative").ToArray();
        foreach (var group in positives.GroupBy(x => x.PersonId))
            if (group.Count() < minimumPositivePerPerson) warnings.Add($"人物 {group.Key} 正样本 {group.Count()}，少于 {minimumPositivePerPerson}");
        if (negatives.Length < minimumNegative) warnings.Add($"排他负样本 {negatives.Length}，少于 {minimumNegative}");
        if (snapshot.ValidationSamples == 0) warnings.Add("验证分区为空");
        if (snapshot.TestSamples == 0) warnings.Add("测试分区为空，不能进行独立门禁");
        if (members.Select(x => x.LeakageGroup).Distinct().Count() < 3) warnings.Add("独立泄漏组过少，可能无法代表真实泛化能力");
        var ready = warnings.Count == 0 && positives.Select(x => x.PersonId).Distinct().Count() >= 2;
        if (!ready && positives.Select(x => x.PersonId).Distinct().Count() < 2) warnings.Add("至少需要两位人物的正样本");
        return new(ready, positives.Select(x => x.PersonId).Distinct().Count(), positives.Length, negatives.Length,
            members.Select(x => x.LeakageGroup).Distinct().Count(), snapshot.ValidationSamples, snapshot.TestSamples, warnings);
    }
}
