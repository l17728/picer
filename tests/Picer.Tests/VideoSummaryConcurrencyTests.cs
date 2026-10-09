using Picer.Core.Data;
using Picer.Core.Imaging;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class VideoSummaryConcurrencyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SameSizeAndTimestampReplacementUsesNewMontage(bool exposeSha)
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-video-montage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new PicerDatabase(Path.Combine(root, "test.db"));
            var repository = new VideoRepository(database);
            var source = database.AddSource(root);
            var path = Path.Combine(root, "video.mp4");
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            var original = repository.Upsert(source.Id, path);
            var originalContent = repository.GetContentId(original.Id);
            var firstFrame = Path.Combine(root, "red.jpg");
            var secondFrame = Path.Combine(root, "blue.jpg");
            using (var red = new RasterImage(32, 32, new RgbPixel(255, 0, 0))) await red.SaveAsJpegAsync(firstFrame, 90);
            using (var blue = new RasterImage(32, 32, new RgbPixel(0, 0, 255))) await blue.SaveAsJpegAsync(secondFrame, 90);
            var analyzer = new MontageAnalyzer();
            var service = new VideoSummaryService(repository, Path.Combine(root, "cache"));
            await service.AnalyzeAsync(exposeSha ? original : original with { Sha256 = null }, analyzer, [new(0, firstFrame)]);
            byte[] replacement = [4, 5, 6];
            await File.WriteAllBytesAsync(path, replacement);
            File.SetLastWriteTimeUtc(path, original.ModifiedUtc);
            var changed = original with { Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(replacement)).ToLowerInvariant() };
            repository.UpsertRemote(changed, source.Id);
            Assert.NotEqual(originalContent, repository.GetContentId(original.Id));
            await service.AnalyzeAsync(exposeSha ? changed : changed with { Sha256 = null }, analyzer, [new(0, secondFrame)]);
            Assert.Equal(2, analyzer.Images.Count);
            Assert.NotEqual(analyzer.Images[0], analyzer.Images[1]);
            Assert.Equal(analyzer.Images[1], repository.GetSummary(original.Id));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class MontageAnalyzer : ICloudVisionAnalyzer
    {
        public List<string> Images { get; } = [];
        public string Provider => "test";
        public string ModelVersion => "test";
        public string PromptVersion => "test";
        public async Task<CloudVisionResult> AnalyzeAsync(CloudVisionRequest request, CancellationToken token)
        {
            await using var input = File.OpenRead(request.Image.Path);
            var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input, token));
            Images.Add(hash);
            return new(hash, [], [], [], []);
        }
    }

    [Fact]
    public async Task SameContentAcrossServiceInstancesUsesOneModelRequestAndOneBillingEvent()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-video-shared-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new PicerDatabase(Path.Combine(root, "test.db"));
            var repository = new VideoRepository(database);
            var source = database.AddSource(root);
            var videos = new List<VideoRecord>();
            for (var index = 0; index < 2; index++)
            {
                var path = Path.Combine(root, index + ".mp4"); File.WriteAllBytes(path, [1, 2, 3]);
                videos.Add(repository.Upsert(source.Id, path));
            }
            var frame = Path.Combine(root, "frame.jpg");
            using (var image = new RasterImage(32, 32)) await image.SaveAsJpegAsync(frame, 82);
            using var databaseGate = new SemaphoreSlim(1);
            async Task Database(Action work, CancellationToken token)
            {
                await databaseGate.WaitAsync(token);
                try { work(); } finally { databaseGate.Release(); }
            }
            var analyzer = new CountingAnalyzer();
            var services = videos.Select(_ => new VideoSummaryService(repository, Path.Combine(root, "cache"), Database)).ToArray();
            await Task.WhenAll(videos.Select((video, index) => services[index].AnalyzeAsync(video, analyzer, [new(0, frame)])));
            Assert.Equal(1, analyzer.Calls);
            using var billed = database.Command("SELECT COUNT(*) FROM analysis_billing_events");
            Assert.Equal(1L, billed.ExecuteScalar());
            Assert.All(videos, video => Assert.True(repository.HasCurrentSummary(video)));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class CountingAnalyzer : ICloudVisionAnalyzer
    {
        private int _calls;
        public int Calls => _calls;
        public string Provider => "test";
        public string ModelVersion => "test";
        public string PromptVersion => "test";
        public async Task<CloudVisionResult> AnalyzeAsync(CloudVisionRequest request, CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(50, token);
            return new("shared", [], [], [], []) { Usage = new(1, 1, .01m) };
        }
    }

    [Fact]
    public async Task DifferentVideosOverlapModelCallsWhileDatabaseWorkIsSerialized()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-video-summary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new PicerDatabase(Path.Combine(root, "test.db"));
            var source = database.AddSource(root);
            var repository = new VideoRepository(database);
            var videos = new List<VideoRecord>();
            for (var i = 0; i < 2; i++)
            {
                var path = Path.Combine(root, $"{i}.mp4");
                File.WriteAllBytes(path, [1, 2, (byte)i]);
                videos.Add(repository.Upsert(source.Id, path));
            }
            var frame = Path.Combine(root, "frame.jpg");
            using (var image = new RasterImage(32, 32)) await image.SaveAsJpegAsync(frame, 82);
            using var gate = new SemaphoreSlim(1);
            async Task Database(Action work, CancellationToken token)
            {
                await gate.WaitAsync(token);
                try { work(); }
                finally { gate.Release(); }
            }
            var analyzer = new OverlappingAnalyzer();
            var service = new VideoSummaryService(repository, Path.Combine(root, "cache"), Database);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await Task.WhenAll(videos.Select(video => service.AnalyzeAsync(video, analyzer,
                [new VideoKeyframe(0, frame)], cancellation.Token)));
            Assert.Equal(2, analyzer.Calls);
            Assert.All(videos, video => Assert.True(repository.HasCurrentSummary(video)));
            using var billed = database.Command("SELECT COUNT(*) FROM analysis_billing_events");
            Assert.Equal(2L, billed.ExecuteScalar());
            using var reservations = database.Command("SELECT COUNT(*) FROM analysis_budget_reservations");
            Assert.Equal(0L, reservations.ExecuteScalar());
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "cache"), "*.tmp"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class OverlappingAnalyzer : ICloudVisionAnalyzer
    {
        private readonly TaskCompletionSource _bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public int Calls => _calls;
        public string Provider => "test";
        public string ModelVersion => "test";
        public string PromptVersion => "test";
        public async Task<CloudVisionResult> AnalyzeAsync(CloudVisionRequest request, CancellationToken token)
        {
            if (Interlocked.Increment(ref _calls) == 2) _bothEntered.TrySetResult();
            await _bothEntered.Task.WaitAsync(token);
            return new("视频测试摘要", [], [], [], []);
        }
    }
}
