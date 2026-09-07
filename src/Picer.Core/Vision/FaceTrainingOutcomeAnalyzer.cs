namespace Picer.Core.Vision;

public sealed record FaceTrainingFailureAnalysis(bool Success, IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Suggestions);

public static class FaceTrainingOutcomeAnalyzer
{
    public static FaceTrainingFailureAnalysis Analyze(FaceTrainingResult result, FaceTrainingReadiness? readiness = null)
    {
        if (result.Applied) return new(true, [], ["当前制品已通过门禁并应用，继续观察线上误归率。"]);
        var reasons = new List<string>(); var suggestions = new List<string>();
        if (readiness is { Ready: false }) { reasons.AddRange(readiness.Warnings); suggestions.Add("补充不同时间、地点和视频来源的人脸正样本及排他负样本。"); }
        if (result.FalseAcceptRate > .01) { reasons.Add($"误识率 {result.FalseAcceptRate:P2} 超过 1% 门禁"); suggestions.Add("增加高相似异人排他负样本，并提高困难负样本覆盖。"); }
        if (result.FalseRejectRate > .02) { reasons.Add($"漏识率 {result.FalseRejectRate:P2} 偏高"); suggestions.Add("补充侧脸、小脸、逆光和低质量正样本，检查阈值校准。"); }
        if (result.ExcludedSameSource > result.SelectedTrainingSamples && result.ExcludedSameSource > 0) { reasons.Add("同源/连拍泄漏组剔除过多"); suggestions.Add("导入更多独立拍摄来源，避免训练集只包含同一连拍组。"); }
        if (reasons.Count == 0) reasons.Add("平台返回未通过门禁，但未提供可分类指标原因");
        if (suggestions.Count == 0) suggestions.Add("查看统一模型平台的任务日志和冻结快照报告。");
        return new(false, reasons.Distinct().ToArray(), suggestions.Distinct().ToArray());
    }
}
