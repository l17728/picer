namespace Picer.Core.Vision;

public enum FaceModelActivationState { Preparing, Backfilling, Evaluating, Ready, Blocked }
public sealed record FaceModelActivationReport(string ModelVersion, FaceModelActivationState State,
    int TotalItems, int VectorItems, double Coverage, double Accuracy, double BaselineAccuracy, string Reason);

/// <summary>新模型只能在覆盖率和新旧评测门禁同时通过后激活。</summary>
public static class FaceModelActivationGate
{
    public static FaceModelActivationReport Evaluate(string modelVersion, int totalItems, int vectorItems,
        double accuracy, double baselineAccuracy, double minimumCoverage = .99, double minimumAccuracy = .90)
    {
        var coverage = totalItems <= 0 ? 0 : (double)vectorItems / totalItems;
        if (coverage < minimumCoverage)
            return new(modelVersion, FaceModelActivationState.Blocked, totalItems, vectorItems, coverage, accuracy, baselineAccuracy, $"向量覆盖率 {coverage:P1} 低于 {minimumCoverage:P1}");
        if (accuracy < minimumAccuracy)
            return new(modelVersion, FaceModelActivationState.Blocked, totalItems, vectorItems, coverage, accuracy, baselineAccuracy, $"新模型准确率 {accuracy:P1} 低于 {minimumAccuracy:P1}");
        if (accuracy < baselineAccuracy)
            return new(modelVersion, FaceModelActivationState.Blocked, totalItems, vectorItems, coverage, accuracy, baselineAccuracy, "新模型评测结果劣于当前模型");
        return new(modelVersion, FaceModelActivationState.Ready, totalItems, vectorItems, coverage, accuracy, baselineAccuracy, "覆盖率和新旧模型评测均通过");
    }
}
