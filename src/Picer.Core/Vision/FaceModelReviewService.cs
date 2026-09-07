namespace Picer.Core.Vision;

public sealed record FaceModelProfile(string ModelId, string Version, string License,
    long PackageBytes, double CpuP95Ms, double MemoryMb, double DetectionRecall,
    double MisassignmentRate, bool DirectMlSupported, bool Quantized,
    double MigrationCostScore = 0);

public sealed record FaceModelReviewResult(string BaselineModelId, string CandidateModelId,
    bool AdapterInsufficient, bool Eligible, IReadOnlyList<string> Reasons);

/// <summary>Safety gate for replacing a face backbone; it never downloads or activates models.</summary>
public static class FaceModelReviewService
{
    public static FaceModelReviewResult Review(FaceModelProfile baseline, FaceModelProfile candidate,
        bool adapterInsufficient, double maxRecallDrop = .01, double maxMisassignmentIncrease = .005)
    {
        var reasons = new List<string>();
        if (!adapterInsufficient) reasons.Add("轻量适配器尚未证明不足，禁止评审底层模型替换");
        if (string.IsNullOrWhiteSpace(candidate.License) || candidate.License.Equals("unknown", StringComparison.OrdinalIgnoreCase)) reasons.Add("候选模型授权未知");
        if (candidate.DetectionRecall < baseline.DetectionRecall - maxRecallDrop) reasons.Add("检测召回率下降超过门限");
        if (candidate.MisassignmentRate > baseline.MisassignmentRate + maxMisassignmentIncrease) reasons.Add("身份误归率上升超过门限");
        if (candidate.CpuP95Ms > baseline.CpuP95Ms * 1.5) reasons.Add("CPU P95 延迟超过基线 1.5 倍");
        if (candidate.MemoryMb > baseline.MemoryMb * 1.5) reasons.Add("内存占用超过基线 1.5 倍");
        if (!candidate.DirectMlSupported && baseline.DirectMlSupported) reasons.Add("失去 DirectML 支持");
        if (candidate.MigrationCostScore > .8) reasons.Add("迁移成本过高");
        return new(baseline.ModelId, candidate.ModelId, adapterInsufficient, reasons.Count == 0, reasons);
    }
}
