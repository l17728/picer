using System.Text.Json;

namespace Picer.ModelServer;

public sealed record ModelCandidateRound(string ModelVersion, string AlgorithmId, int Iterations,
    double ValidationAccuracy, double FalseAcceptRate, double FalseRejectRate,
    bool GatePassed, string? ArtifactId, DateTime CreatedUtc, string? Scenario = null,
    string? EvaluationId = null);

public sealed record ModelCandidateRanking(string ModelVersion, string AlgorithmId, bool Available,
    int Rounds, int PassedRounds, double BestValidationAccuracy, double BestFar,
    double BestFrr, bool Retained, string? LastArtifactId, int Rank = 0,
    int Wins = 0, double CompositeScore = 0, int LastRound = 0, bool SingleModelSelected = false);

public sealed record ModelCandidateRoundResult(string EvaluationId, string ModelVersion,
    string AlgorithmId, int Rank, double Score, double ValidationAccuracy,
    double FalseAcceptRate, double FalseRejectRate, bool GatePassed, string? ArtifactId,
    DateTime CreatedUtc, string? Scenario);

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
            var rounds = ReadRounds();
            var roundGroups = rounds.Where(r => string.IsNullOrWhiteSpace(scenario) ||
                    string.Equals(r.Scenario, scenario, StringComparison.OrdinalIgnoreCase))
                .GroupBy(r => $"{r.Scenario}|{RoundKey(r)}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(Score).ThenByDescending(r => r.ValidationAccuracy)
                    .ThenBy(r => r.ModelVersion, StringComparer.OrdinalIgnoreCase).ToArray())
                .ToArray();
            var measured = adapters.Select(adapter =>
            {
                var values = rounds.Where(r => string.Equals(r.ModelVersion, adapter.ModelVersion, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(scenario) || string.Equals(r.Scenario, scenario, StringComparison.OrdinalIgnoreCase))).ToArray();
                var best = values.OrderByDescending(r => Score(r)).FirstOrDefault();
                var composite = values.Length == 0 ? 0 : values.Average(Score);
                var wins = roundGroups.Count(group => group.Length > 0 &&
                    string.Equals(group[0].ModelVersion, adapter.ModelVersion, StringComparison.OrdinalIgnoreCase));
                return new ModelCandidateRanking(adapter.ModelVersion, adapter.Id, adapter.IsAvailable,
                    values.Length, values.Count(r => r.GatePassed), best?.ValidationAccuracy ?? 0,
                    best?.FalseAcceptRate ?? 1, best?.FalseRejectRate ?? 1, false, best?.ArtifactId,
                    Rank: 0, Wins: wins, CompositeScore: composite, LastRound: values.Length == 0 ? 0 : values.Max(r => r.Iterations));
            }).OrderByDescending(x => x.Available).ThenByDescending(x => Score(x)).ThenByDescending(x => x.Wins)
                .ThenByDescending(x => x.BestValidationAccuracy).ToList();
            measured = measured.Select((value, index) => value with { Rank = index + 1 }).ToList();
            var available = measured.Where(x => x.Available).Select(x => x.ModelVersion).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var winner = measured.FirstOrDefault(x => x.Available);
            var comparablePassedRounds = winner is null ? 0 : roundGroups.Count(group =>
                available.IsSubsetOf(group.Select(x => x.ModelVersion)) &&
                group.Any(x => x.ModelVersion.Equals(winner.ModelVersion, StringComparison.OrdinalIgnoreCase) &&
                    x.GatePassed && x.ArtifactId is not null && x.ValidationAccuracy >= .90 &&
                    x.FalseAcceptRate <= .01 && x.FalseRejectRate <= .10));
            var single = available.Count >= 2 && comparablePassedRounds >= 20;
            var retainedCount = single ? 1 : 3;
            var retained = measured.Take(retainedCount).Select(x => x.ModelVersion).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Keep only the current top three as primary training candidates.
            // Models ranked 4+ remain visible as backup candidates and are not
            // selected by subsequent training submissions.
            return measured.Select(x => x with { Retained = retained.Contains(x.ModelVersion), SingleModelSelected = single && x.Rank == 1 }).ToArray();
        }
    }

    public IReadOnlyList<ModelCandidateRoundResult> Rounds(string? scenario = null)
    {
        lock (_gate)
        {
            var rounds = ReadRounds().Where(r => string.IsNullOrWhiteSpace(scenario) ||
                string.Equals(r.Scenario, scenario, StringComparison.OrdinalIgnoreCase)).ToArray();
            return rounds.GroupBy(r => $"{r.Scenario}|{RoundKey(r)}", StringComparer.OrdinalIgnoreCase).SelectMany(group =>
                group.OrderByDescending(Score).ThenBy(r => r.ModelVersion, StringComparer.OrdinalIgnoreCase)
                    .Select((r, index) => new ModelCandidateRoundResult(RoundKey(r), r.ModelVersion,
                        r.AlgorithmId, index + 1, Score(r), r.ValidationAccuracy, r.FalseAcceptRate,
                        r.FalseRejectRate, r.GatePassed, r.ArtifactId, r.CreatedUtc, r.Scenario)))
                .OrderByDescending(r => r.CreatedUtc).ToArray();
        }
    }

    private ModelCandidateRound[] ReadRounds() => File.Exists(_path)
        ? File.ReadLines(_path).Select(line => JsonSerializer.Deserialize<ModelCandidateRound>(line, _json))
            .Where(x => x is not null).Cast<ModelCandidateRound>()
            .GroupBy(r => $"{r.Scenario}|{RoundKey(r)}|{r.ModelVersion}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(r => r.CreatedUtc).First()).ToArray()
        : [];

    private static string RoundKey(ModelCandidateRound round) =>
        string.IsNullOrWhiteSpace(round.EvaluationId)
            ? $"legacy:{round.ArtifactId ?? round.CreatedUtc.Ticks.ToString()}"
            : round.EvaluationId!;

    private static double Score(ModelCandidateRound round) => round.ValidationAccuracy - round.FalseAcceptRate - round.FalseRejectRate * .25;
    private static double Score(ModelCandidateRanking ranking) => ranking.BestValidationAccuracy - ranking.BestFar - ranking.BestFrr * .25;
}
