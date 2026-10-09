using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Geocoding;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class PhotoSummaryConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-photo-concurrency-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _database;
    private readonly SemaphoreSlim _databaseGate = new(1);

    public PhotoSummaryConcurrencyTests()
    {
        Directory.CreateDirectory(_root);
        _database = new PicerDatabase(Path.Combine(_root, "test.db"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SeparateServicesShareOneRequestForSameContent(bool duplicateReference, bool cancelWaiter)
    {
        var first = AddImage("first.jpg", 'a');
        var second = duplicateReference ? AddImage("copy.jpg", 'a') : first;
        var analyzer = new BlockingAnalyzer(1);
        var firstService = Service(analyzer);
        var secondService = Service(analyzer);
        var running = Analyze(firstService, first);
        await analyzer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var waiting = Analyze(secondService, second, cancellation.Token);
        if (cancelWaiter)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        analyzer.Release.TrySetResult();
        var completed = await running.WaitAsync(TimeSpan.FromSeconds(5));
        var reused = await (cancelWaiter ? Analyze(secondService, second) : waiting).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, completed.Completed);
        Assert.Equal(1, reused.Skipped);
        Assert.Equal(1, analyzer.Calls);
        using var count = _database.Command("SELECT COUNT(*) FROM analysis_billing_events");
        Assert.Equal(1L, count.ExecuteScalar());
    }

    [Fact]
    public async Task SeparateContentsCanCallModelsAtTheSameTime()
    {
        var first = AddImage("first.jpg", 'a');
        var second = AddImage("second.jpg", 'b');
        var analyzer = new BlockingAnalyzer(2);
        var running = Task.WhenAll(Analyze(Service(analyzer), first), Analyze(Service(analyzer), second));
        try { await analyzer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { analyzer.Release.TrySetResult(); }
        var results = await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, result => Assert.Equal(1, result.Completed));
        Assert.Equal(2, analyzer.Calls);
    }

    private ImageRecord AddImage(string name, char hash) => _database.UpsertImage(new ImageRecord
    {
        SourceId = _database.AddSource(_root).Id,
        Path = Path.Combine(_root, name), FileSize = 3,
        ModifiedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Sha256 = new string(hash, 64), Status = ImageStatus.Active
    });

    private CloudVisionAnalysisService Service(ICloudVisionAnalyzer analyzer) => new(new(_database), analyzer,
        new NoGeocoder(), databaseWork: async (work, token) =>
        {
            await _databaseGate.WaitAsync(token);
            try { work(); } finally { _databaseGate.Release(); }
        }, allowConcurrent: true);

    private static Task<CloudVisionBatchResult> Analyze(CloudVisionAnalysisService service, ImageRecord image,
        CancellationToken token = default) => service.AnalyzeAsync([(image, (IReadOnlyList<VisualConceptScore>)[], "test")],
            cancellationToken: token);

    private sealed class BlockingAnalyzer(int requiredCalls) : ICloudVisionAnalyzer
    {
        private int _calls;
        public int Calls => _calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Provider => "test";
        public string ModelVersion => "test-v1";
        public string PromptVersion => "v1";
        public async Task<CloudVisionResult> AnalyzeAsync(CloudVisionRequest request, CancellationToken token)
        {
            if (Interlocked.Increment(ref _calls) >= requiredCalls) Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
            return new("shared description", [], [], [], []) { Usage = new(1, 1, .01m) };
        }
    }

    private sealed class NoGeocoder : IReverseGeocoder
    {
        public Task<ReverseGeocodingResult?> ReverseAsync(double latitude, double longitude, CancellationToken cancellationToken = default)
            => Task.FromResult<ReverseGeocodingResult?>(null);
        public Task<GeoBounds?> ResolvePlaceAsync(string query, CancellationToken cancellationToken = default)
            => Task.FromResult<GeoBounds?>(null);
    }

    public void Dispose()
    {
        _database.Dispose();
        _databaseGate.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
