using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Imaging;
using Picer.Core.Security;
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

    [Fact]
    public async Task RemoteSummaryMontageIsEncryptedAtRestAndTemporaryPlaintextIsRemoved()
    {
        var frames = await CreateFramesAsync();
        var cache = Path.Combine(_root, "encrypted-cache");
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var analyzer = new Analyzer { CaptureRequestImage = true };
        try
        {
            using (var service = new VideoSummaryService(_repository, cache, montageCipher: new MediaFileCipher(key)))
            {
                await service.AnalyzeAsync(_video, analyzer, frames);
                Assert.NotNull(analyzer.Request);
                Assert.False(File.Exists(analyzer.Request!.Image.Path));
            }

            var montage = Assert.Single(Directory.EnumerateFiles(cache, "video-*.jpg"));
            var stored = await File.ReadAllBytesAsync(montage);
            using var cipher = new MediaFileCipher(key);
            Assert.True(cipher.IsEncrypted(stored));
            var plaintext = cipher.UnprotectOrReadLegacy(stored, Path.GetFileName(montage), out _);
            try
            {
                Assert.Equal([0xff, 0xd8], plaintext[..2].Select(value => (int)value).ToArray());
                Assert.Equal([0xff, 0xd8], analyzer.CapturedImage![..2].Select(value => (int)value).ToArray());
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(stored);
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); }
    }

    [Fact]
    public async Task ExistingPlaintextSummaryMontageIsMigrated()
    {
        var frames = await CreateFramesAsync();
        var cache = Path.Combine(_root, "legacy-cache");
        using (var legacy = new VideoSummaryService(_repository, cache))
            await legacy.AnalyzeAsync(_video, _analyzer, frames);
        var montage = Assert.Single(Directory.EnumerateFiles(cache, "video-*.jpg"));
        Assert.Equal([0xff, 0xd8], (await File.ReadAllBytesAsync(montage))[..2].Select(value => (int)value).ToArray());

        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        try
        {
            using var encrypted = new VideoSummaryService(_repository, cache, montageCipher: new MediaFileCipher(key));
            Assert.Equal((1, 0), encrypted.ProtectExistingMontages());
            var stored = await File.ReadAllBytesAsync(montage);
            using var verifier = new MediaFileCipher(key);
            try { Assert.True(verifier.IsEncrypted(stored)); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(stored); }
            Assert.Equal((0, 0), encrypted.ProtectExistingMontages());
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); }
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
        public bool CaptureRequestImage { get; init; }
        public byte[]? CapturedImage { get; private set; }
        public Task<CloudVisionResult> AnalyzeAsync(CloudVisionRequest request, CancellationToken token)
        {
            Request = request;
            if (CaptureRequestImage) CapturedImage = File.ReadAllBytes(request.Image.Path);
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
