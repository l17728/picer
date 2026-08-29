namespace Picer.Core.Vision;

public sealed record CloudVisionRoutingDecision(bool ShouldAnalyze, string Reason);

public sealed class CloudVisionRoutingPolicy(float lowConfidenceThreshold = 0.65f)
{
    public CloudVisionRoutingDecision Evaluate(
        IReadOnlyList<VisualConceptScore> concepts,
        int faceCount,
        bool userRequested = false,
        bool isFavorite = false)
    {
        if (userRequested) return new(true, "user-requested");
        if (isFavorite) return new(true, "favorite");
        if (faceCount > 1) return new(true, "multiple-people");
        if (concepts.Count == 0) return new(true, "no-local-concepts");
        if (concepts.Any(concept => concept.Confidence < lowConfidenceThreshold))
            return new(true, "low-local-confidence");
        return new(false, "local-analysis-sufficient");
    }
}
