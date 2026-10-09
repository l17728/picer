using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class AnalysisBillingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-billing-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _database;
    public AnalysisBillingTests()
    {
        Directory.CreateDirectory(_root);
        _database = new PicerDatabase(Path.Combine(_root, "picer.db"));
    }

    [Fact]
    public void ReplacingDescriptionAndFailureDoNotErasePriorRequestCosts()
    {
        var image = _database.UpsertImage(new ImageRecord { SourceId = _database.AddSource(_root).Id,
            Path = Path.Combine(_root, "photo.jpg"), ModifiedUtc = DateTime.UtcNow, FileSize = 3 });
        var repository = new CloudVisionRepository(_database);
        var analyzer = new Analyzer();
        var result = new CloudVisionResult("description", [], [], [], []) { Usage = new CloudVisionUsage(10, 20, .2m) };
        repository.Save(image, analyzer, "none", "test", result, "first");
        repository.Save(image, analyzer, "none", "test", result, "first");
        repository.Save(image, analyzer, "none", "test", result, "second");
        repository.SaveFailure(image, analyzer, "none", "test", "network");
        Assert.Equal(.4m, repository.GetEstimatedCostSince(DateTime.UtcNow.AddDays(-1)));
        using var query = _database.Command("SELECT COUNT(*) FROM analysis_billing_events");
        Assert.Equal(2L, query.ExecuteScalar());
    }

    [Fact]
    public void ConcurrentConnectionsCannotReserveTheSameRemainingBudget()
    {
        using var second = new PicerDatabase(Path.Combine(_root, "picer.db"));
        var firstBilling = new AnalysisBillingRepository(_database);
        var secondBilling = new AnalysisBillingRepository(second);
        var outcomes = new bool[2];
        Parallel.Invoke(() => outcomes[0] = firstBilling.TryReserve("a", .7m, 1m, 5m),
            () => outcomes[1] = secondBilling.TryReserve("b", .7m, 1m, 5m));
        Assert.Single(outcomes.Where(value => value));
        firstBilling.Release("a"); secondBilling.Release("b");
        Assert.True(firstBilling.TryReserve("c", .7m, 1m, 5m));
        firstBilling.Record("c", new Analyzer(), new CloudVisionUsage(1, 1, .8m));
        Assert.False(secondBilling.TryReserve("d", .3m, 1m, 5m));
    }

    [Fact]
    public void ExpiredReservationDoesNotPermanentlyBlockBudget()
    {
        var billing = new AnalysisBillingRepository(_database);
        Assert.True(billing.TryReserve("old", 1m, 1m, 1m));
        using (var expire = _database.Command("UPDATE analysis_budget_reservations SET expires_utc='2000-01-01'")) expire.ExecuteNonQuery();
        Assert.True(billing.TryReserve("new", 1m, 1m, 1m));
    }

    private sealed class Analyzer : ICloudVisionAnalyzer
    {
        public string Provider => "test";
        public string ModelVersion => "test-v1";
        public string PromptVersion => "v1";
        public Task<CloudVisionResult> AnalyzeAsync(CloudVisionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    public void Dispose() { _database.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
}
