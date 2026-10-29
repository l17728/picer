using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Picer.App.Infrastructure;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Geocoding;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Vision;

namespace Picer.E2E;

public sealed class PhotoAnalysisSyncConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-photo-sync-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PhotoAnalysisCanSaveWhileMetadataSnapshotIsBetweenScheduledBatches()
    {
        using var database = CreateDatabase();
        var remote = CreateEdits();
        var firstBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeSync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returnResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduled = 0;
        using var sync = new IndexSyncClient(new HttpClient(new EditHandler(remote, async token =>
        {
            requestStarted.TrySetResult();
            await returnResponse.Task.WaitAsync(token);
        })) { BaseAddress = new Uri("https://fixture.invalid/") },
            databaseWork: async (work, token) =>
            {
                await UiWork.RunDatabaseAsync(work, token);
                if (Interlocked.Increment(ref scheduled) == 1)
                {
                    firstBatch.SetResult();
                    await resumeSync.Task.WaitAsync(token);
                }
            });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var syncing = sync.PushMetadataEditsAsync(database, deadline.Token);
        try
        {
            await requestStarted.Task.WaitAsync(deadline.Token);
            // A slow network response must not own the UI database scheduler.
            var source = await UiWork.RunDatabaseAsync(() => database.AddSource(_root), deadline.Token)
                .WaitAsync(TimeSpan.FromSeconds(5));
            returnResponse.SetResult();
            await firstBatch.Task.WaitAsync(deadline.Token);
            var merged = await UiWork.RunDatabaseAsync(() => database.GetMetadataEdits().Count, deadline.Token);
            Assert.InRange(merged, 1, remote.Length - 1);
            Assert.False(syncing.IsCompleted);

            var image = await UiWork.RunDatabaseAsync(() => database.UpsertImage(new ImageRecord
            {
                SourceId = source.Id, Path = Path.Combine(_root, "photo.jpg"), FileSize = 3,
                ModifiedUtc = DateTime.UnixEpoch, IndexedUtc = DateTime.UtcNow, Status = ImageStatus.Active
            }), deadline.Token);
            var repository = new CloudVisionRepository(database);
            var analyzer = new Analyzer();
            var analysis = new CloudVisionAnalysisService(repository, analyzer, new Geocoder(),
                databaseWork: (work, token) => UiWork.RunDatabaseAsync(work, token));
            var result = await analysis.AnalyzeAsync([(image, [], "test")], cancellationToken: deadline.Token);

            Assert.Equal(1, result.Completed);
            Assert.Equal(0, result.Failed);
            Assert.Equal(1, analyzer.Calls);
            Assert.NotNull(await UiWork.RunDatabaseAsync(() => repository.GetLatestCompleted(image.Id), deadline.Token));
            Assert.False(syncing.IsCompleted);
        }
        finally
        {
            returnResponse.TrySetResult();
            resumeSync.TrySetResult();
            await syncing;
        }
        Assert.True(scheduled > 1);
        Assert.Equal(remote.Length, database.GetMetadataEdits().Count);
        Assert.Equal(remote.Length, sync.AcknowledgedMetadataOperations.Count);
    }

    [Fact]
    public async Task CancelledPartialMergeCanRetryWithoutLosingAcknowledgmentsOrHoldingTheGate()
    {
        using var database = CreateDatabase();
        var remote = CreateEdits();
        using var cancellation = new CancellationTokenSource();
        var scheduled = 0;
        using var sync = new IndexSyncClient(new HttpClient(new EditHandler(remote)) { BaseAddress = new Uri("https://fixture.invalid/") },
            databaseWork: async (work, token) =>
            {
                await UiWork.RunDatabaseAsync(work, token);
                if (Interlocked.Increment(ref scheduled) == 1) cancellation.Cancel();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync.PushMetadataEditsAsync(database, cancellation.Token));
        Assert.InRange(database.GetMetadataEdits().Count, 1, remote.Length - 1);
        Assert.Empty(sync.AcknowledgedMetadataOperations);
        await sync.PushMetadataEditsAsync(database).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(remote.Length, database.GetMetadataEdits().Count);
        Assert.Equal(remote.Length, sync.AcknowledgedMetadataOperations.Count);
    }

    private PicerDatabase CreateDatabase()
    {
        var database = new PicerDatabase(Path.Combine(_root, "mirror.db"));
        try
        {
            for (var id = 1; id <= 205; id++) database.UpsertMirroredPerson(new(id, "fixture-" + id, 0, 0));
            return database;
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    private static MetadataEdit[] CreateEdits() => Enumerable.Range(1, 205)
        .Select(id => new MetadataEdit("person", id, "description", "remote-fixture", DateTime.UnixEpoch.ToString("O"),
            Guid.NewGuid().ToString("N")) { ServerVersion = 1 }).ToArray();

    private sealed class EditHandler(MetadataEdit[] remote, Func<CancellationToken, Task>? beforeRead = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && beforeRead is not null) await beforeRead(cancellationToken);
            var result = request.Method == HttpMethod.Get ? remote
                : await request.Content!.ReadFromJsonAsync<MetadataEdit[]>(cancellationToken: cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result) };
        }
    }

    private sealed class Analyzer : ICloudVisionAnalyzer
    {
        public string Provider => "fixture";
        public string ModelVersion => "fixture-v1";
        public string PromptVersion => "fixture-v1";
        public int Calls { get; private set; }
        public Task<CloudVisionResult> AnalyzeAsync(CloudVisionRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new CloudVisionResult("fixture", [], [], [], [], null, "none"));
        }
    }

    private sealed class Geocoder : IReverseGeocoder
    {
        public Task<ReverseGeocodingResult?> ReverseAsync(double latitude, double longitude, CancellationToken cancellationToken = default)
            => Task.FromResult<ReverseGeocodingResult?>(null);
        public Task<GeoBounds?> ResolvePlaceAsync(string query, CancellationToken cancellationToken = default)
            => Task.FromResult<GeoBounds?>(null);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
