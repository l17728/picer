using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Geocoding;
using Picer.Core.Imaging;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class DescriptionCleanupFailureTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CleanupFailurePreservesPrimaryOutcomeAndReservationIsRecoverable(bool video, bool cancel)
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-cleanup-db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new PicerDatabase(Path.Combine(root, "test.db"));
            var source = database.AddSource(root);
            using var cancellation = new CancellationTokenSource();
            var analyzer = new Analyzer(cancellation, cancel);
            var postModelWrites = 0;
            var cleanupFailures = 0;
            Task Execute(Action work, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (analyzer.ModelFinished && (cancel || ++postModelWrites > 1))
                {
                    cleanupFailures++;
                    throw new SqliteException("injected cleanup lock", 5);
                }
                work();
                return Task.CompletedTask;
            }

            if (video)
            {
                var path = Path.Combine(root, "video.mp4");
                File.WriteAllBytes(path, [1, 2, 3]);
                var repository = new VideoRepository(database);
                var record = repository.Upsert(source.Id, path);
                var frame = Path.Combine(root, "frame.jpg");
                using (var raster = new RasterImage(32, 32)) await raster.SaveAsJpegAsync(frame, 82);
                var service = new VideoSummaryService(repository, Path.Combine(root, "cache"), Execute);
                Task<CloudVisionResult> Run() => service.AnalyzeAsync(record, analyzer, [new VideoKeyframe(0, frame)], cancellation.Token);
                if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
                else
                {
                    Assert.Equal("completed", (await Run()).Summary);
                    Assert.NotNull(repository.GetCurrentSummaryResult(record, analyzer));
                }
            }
            else
            {
                var image = database.UpsertImage(new ImageRecord { SourceId = source.Id,
                    Path = Path.Combine(root, "photo.jpg"), FileSize = 3, ModifiedUtc = DateTime.UtcNow, Status = ImageStatus.Active });
                var repository = new CloudVisionRepository(database);
                var service = new CloudVisionAnalysisService(repository, analyzer, new Geocoder(), databaseWork: Execute);
                Task<CloudVisionBatchResult> Run() => service.AnalyzeAsync([(image, (IReadOnlyList<VisualConceptScore>)[], "test")], cancellationToken: cancellation.Token);
                if (cancel)
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
                    Assert.Null(repository.GetLatestCompleted(image.Id));
                    Assert.Equal("processing", repository.Get(image.Id, analyzer.Provider)!.Value.Status);
                }
                else
                {
                    var result = await Run();
                    Assert.Equal(1, result.Completed);
                    Assert.Equal(0, result.Failed);
                    Assert.NotNull(repository.GetLatestCompleted(image.Id));
                }
            }
            Assert.Equal(cancel && !video ? 2 : 1, cleanupFailures);
            using (var count = database.Command("SELECT COUNT(*) FROM analysis_budget_reservations"))
                Assert.Equal(cancel ? 1L : 0L, count.ExecuteScalar());
            if (cancel)
            {
                using (var expiry = database.Command("SELECT expires_utc FROM analysis_budget_reservations"))
                {
                    var expiration = DateTimeOffset.Parse((string)expiry.ExecuteScalar()!, System.Globalization.CultureInfo.InvariantCulture);
                    Assert.InRange(expiration, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1).AddMinutes(1));
                }
                // Simulate the expiry boundary without sleeping; the real reservation
                // path must remove the abandoned row before evaluating the new budget.
                using (var expire = database.Command("UPDATE analysis_budget_reservations SET expires_utc=$expiry"))
                {
                    expire.Parameters.AddWithValue("$expiry", DateTime.UtcNow.AddMinutes(-1).ToString("o"));
                    expire.ExecuteNonQuery();
                }
                var billing = new AnalysisBillingRepository(database);
                Assert.True(billing.TryReserve("next-request", 1m, 1m, 1m));
                using var count = database.Command("SELECT COUNT(*) FROM analysis_budget_reservations");
                Assert.Equal(1L, count.ExecuteScalar());
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class Analyzer(CancellationTokenSource cancellation, bool cancel) : ICloudVisionAnalyzer
    {
        public bool ModelFinished { get; private set; }
        public string Provider => "cleanup-test";
        public string ModelVersion => "test-v1";
        public string PromptVersion => "v1";
        public Task<CloudVisionResult> AnalyzeAsync(CloudVisionRequest request, CancellationToken token)
        {
            ModelFinished = true;
            if (!cancel) return Task.FromResult(new CloudVisionResult("completed", [], [], [], []));
            cancellation.Cancel();
            return Task.FromCanceled<CloudVisionResult>(token);
        }
    }

    private sealed class Geocoder : IReverseGeocoder
    {
        public Task<ReverseGeocodingResult?> ReverseAsync(double latitude, double longitude, CancellationToken cancellationToken = default) => Task.FromResult<ReverseGeocodingResult?>(null);
        public Task<GeoBounds?> ResolvePlaceAsync(string query, CancellationToken cancellationToken = default) => Task.FromResult<GeoBounds?>(null);
    }
}
