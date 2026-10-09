using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Imaging;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class VideoSummaryPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-video-policy-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _database;
    private readonly VideoRepository _repository;
    private readonly VideoRecord _video;
    private readonly Analyzer _analyzer = new();
    private readonly VideoSummaryService _service;

    public VideoSummaryPolicyTests()
    {
        Directory.CreateDirectory(_root);
        _database = new PicerDatabase(Path.Combine(_root, "test.db"));
        _repository = new VideoRepository(_database);
        var source = _database.AddSource(_root);
        var path = Path.Combine(_root, "video.mp4");
        File.WriteAllBytes(path, [1, 2, 3]);
        _video = _repository.Upsert(source.Id, path) with { Gps = "31.123456,121.654321" };
        _service = new VideoSummaryService(_repository, Path.Combine(_root, "cache"));
    }

    [Theory]
    [InlineData(false, 100)]
    [InlineData(true, 0)]
    [InlineData(true, -1)]
    public async Task DisabledOrZeroLimitNeverPreprocessesOrCallsModel(bool enabled, int maximum)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.AnalyzeAsync(_video, _analyzer,
            [], runPolicy: new(enabled, maximum)));
        Assert.Null(_analyzer.Request);
        Assert.False(Directory.Exists(Path.Combine(_root, "cache")));
        AssertNoBilling();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreciseLocationRequiresExplicitPolicy(bool allowGps)
    {
        var frames = await CreateFramesAsync();
        await _service.AnalyzeAsync(_video, _analyzer, frames,
            runPolicy: new(Enabled: true, IncludePreciseGps: allowGps));
        Assert.NotNull(_analyzer.Request);
        Assert.Equal(allowGps, _analyzer.Request!.AdditionalContext!.Contains(_video.Gps!, StringComparison.Ordinal));
        Assert.Contains("不得根据人脸猜测", _analyzer.Request.AdditionalContext);
    }

    [Fact]
    public async Task ConfiguredBudgetBlocksModelWithoutLeavingReservation()
    {
        var frames = await CreateFramesAsync();
        await Assert.ThrowsAsync<AnalysisBudgetExceededException>(() => _service.AnalyzeAsync(_video, _analyzer,
            frames, runPolicy: new(Enabled: true, DailyBudgetYuan: 0m)));
        Assert.Null(_analyzer.Request);
        AssertNoBilling();
    }

    private async Task<VideoKeyframe[]> CreateFramesAsync()
    {
        var path = Path.Combine(_root, "frame.jpg");
        using var image = new RasterImage(32, 32);
        await image.SaveAsJpegAsync(path, 82);
        return [new(0, path)];
    }

    private void AssertNoBilling()
    {
        using var query = _database.Command("SELECT (SELECT COUNT(*) FROM analysis_billing_events) + (SELECT COUNT(*) FROM analysis_budget_reservations)");
        Assert.Equal(0L, query.ExecuteScalar());
    }

    private sealed class Analyzer : ICloudVisionAnalyzer
    {
        public string Provider => "test";
        public string ModelVersion => "test-v1";
        public string PromptVersion => "v1";
        public CloudVisionRequest? Request { get; private set; }
        public Task<CloudVisionResult> AnalyzeAsync(CloudVisionRequest request, CancellationToken token)
        {
            Request = request;
            return Task.FromResult(new CloudVisionResult("description", [], [], [], []));
        }
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
