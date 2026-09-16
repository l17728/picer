using System.Text.Json;

namespace Picer.ModelServer;

public sealed record ModelCandidateRound(string ModelVersion, string AlgorithmId, int Iterations,
    double ValidationAccuracy, double FalseAcceptRate, double FalseRejectRate,
    bool GatePassed, string? ArtifactId, DateTime CreatedUtc, string? Scenario = null);

public sealed record ModelCandidateRanking(string ModelVersion, string AlgorithmId, bool Available,
    int Rounds, int PassedRounds, double BestValidationAccuracy, double BestFar,
    double BestFrr, bool Retained, string? LastArtifactId);

/// <summary>Durable ranking memory for model candidates. One round is one
/// nine-attempt Agent run; only measured artifacts are admitted.</summary>
public sealed class ModelCandidateRankingStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public ModelCandidateRankingStore()
    {
        var root = Environment.GetEnvironmentVariable("PICER_TRAINING_ARCHIVE_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "training-archive");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "model-rounds.jsonl");
    }

    public void Append(ModelCandidateRound round)
    {
        lock (_gate)
            File.AppendAllText(_path, JsonSerializer.Serialize(round, _json) + Environment.NewLine);
    }

    public IReadOnlyList<ModelCandidateRanking> Rank(IEnumerable<ITrainingAlgorithmAdapter> adapters, string? scenario = null)
    {
        lock (_gate)
        {
            IReadOnlyList<ModelCandidateRound> rounds = File.Exists(_path)
                ? File.ReadLines(_path).Select(line => JsonSerializer.Deserialize<ModelCandidateRound>(line, _json))
                    .Where(x => x is not null).Cast<ModelCandidateRound>().ToArray()
                : [];
            var total = string.IsNullOrWhiteSpace(scenario) ? rounds.Count : rounds.Count(r => string.Equals(r.Scenario, scenario, StringComparison.OrdinalIgnoreCase));
            var retainTopThree = total >= 20;
            var measured = adapters.Select(adapter =>
            {
                var values = rounds.Where(r => string.Equals(r.ModelVersion, adapter.ModelVersion, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(scenario) || string.Equals(r.Scenario, scenario, StringComparison.OrdinalIgnoreCase))).ToArray();
                var best = values.OrderByDescending(r => Score(r)).FirstOrDefault();
                return new ModelCandidateRanking(adapter.ModelVersion, adapter.Id, adapter.IsAvailable,
                    values.Length, values.Count(r => r.GatePassed), best?.ValidationAccuracy ?? 0,
                    best?.FalseAcceptRate ?? 1, best?.FalseRejectRate ?? 1, false, best?.ArtifactId);
            }).OrderByDescending(x => Score(x)).ToList();
            var retained = measured.Take(3).Select(x => x.ModelVersion).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return measured.Select(x => x with { Retained = !retainTopThree || retained.Contains(x.ModelVersion) }).ToArray();
        }
    }

    private static double Score(ModelCandidateRound round) => round.ValidationAccuracy - round.FalseAcceptRate - round.FalseRejectRate * .25;
    private static double Score(ModelCandidateRanking ranking) => ranking.BestValidationAccuracy - ranking.BestFar - ranking.BestFrr * .25;
}
