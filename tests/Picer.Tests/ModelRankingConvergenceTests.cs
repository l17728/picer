using Picer.ModelServer;

namespace Picer.Tests;

public sealed class ModelRankingConvergenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-ranking-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previous = Environment.GetEnvironmentVariable("PICER_TRAINING_ARCHIVE_ROOT");
    private readonly ModelCandidateRankingStore _store;
    private static readonly ITrainingAlgorithmAdapter[] Models = [new Adapter("a"), new Adapter("b"), new Adapter("c")];
    public ModelRankingConvergenceTests()
    {
        Environment.SetEnvironmentVariable("PICER_TRAINING_ARCHIVE_ROOT", _root);
        _store = new ModelCandidateRankingStore();
    }
    private void Add(string model, string evaluation, bool passed = true) =>
        _store.Append(new(model, "calibration", 9, model == "a" ? .99 : .95, passed ? 0 : .2, .01,
            passed, "artifact-" + model + evaluation, DateTime.UtcNow, EvaluationId: evaluation));

    [Fact]
    public void TwentyRoundsOfOnlyOneModelCannotSelectUniqueWinner()
    {
        for (var i = 0; i < 20; i++) Add("a", i.ToString());
        var ranked = _store.Rank(Models);
        Assert.Equal(3, ranked.Count(x => x.Retained));
        Assert.DoesNotContain(ranked, x => x.SingleModelSelected);
    }
    [Fact]
    public void DuplicateReplaysDoNotCountAsNewRounds()
    {
        for (var i = 0; i < 20; i++) Add("a", "same");
        Assert.Equal(1, _store.Rank(Models).Single(x => x.ModelVersion == "a").Rounds);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TwentyComparableRoundsRequireQualityGate(bool passed)
    {
        for (var i = 0; i < 20; i++) foreach (var model in Models) Add(model.ModelVersion, i.ToString(), passed);
        var ranked = _store.Rank(Models);
        Assert.Equal(passed ? 1 : 3, ranked.Count(x => x.Retained));
        Assert.Equal(passed, ranked.Any(x => x.SingleModelSelected));
    }
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PICER_TRAINING_ARCHIVE_ROOT", _previous);
        Directory.Delete(_root, true);
    }
    private sealed record Adapter(string ModelVersion) : ITrainingAlgorithmAdapter
    {
        public string Id => ModelVersion;
        public string DisplayName => ModelVersion;
        public bool IsAvailable => true;
        public bool Supports(string type) => true;
    }
}
