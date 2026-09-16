namespace Picer.ModelServer;

public sealed record ModelEnsembleMember(string ModelVersion, string AlgorithmId, double Weight, string Role);

public static class ModelEnsemblePolicy
{
    public static IReadOnlyList<ModelEnsembleMember> Build(IReadOnlyList<ModelCandidateRanking> rankings)
    {
        var retained = rankings.Where(x => x.Retained && x.Available).Take(3).ToArray();
        if (retained.Length == 0) return [];
        var raw = retained.Select(x => Math.Max(.001, x.BestValidationAccuracy - x.BestFar)).ToArray();
        var sum = raw.Sum();
        return retained.Select((x, i) => new ModelEnsembleMember(x.ModelVersion, x.AlgorithmId,
            Math.Round(raw[i] / sum, 4), i == 0 ? "primary" : "corroborator")).ToArray();
    }
}
