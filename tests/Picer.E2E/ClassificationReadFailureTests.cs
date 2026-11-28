using System.IO;
using System.Net;
using System.Net.Http;
using Picer.App;
using Picer.Core.Data;
using Picer.Core.Classification;
using System.Net.Http.Json;

namespace Picer.E2E;

public sealed class ClassificationReadFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-classification-read-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _db;
    public ClassificationReadFailureTests() => _db = new(Path.Combine(_root, "index.db"));
    [Fact]
    public async Task AcceptAndMatchRecoversLostResponseUsingSameReview()
    {
        var received = new List<ClassificationProposalReview>();
        var expected = new ClassificationProposalReview(Guid.NewGuid().ToString("N"), "proposal", 3, 4, "accept");
        var job = new ClassificationJob("original", "queued", 1, 0, 0, 1, 0, 0, 100);
        using var http = new HttpClient(new AsyncCallbackHandler(async request =>
        {
            Assert.Equal("/proposals/accept-and-match", request.RequestUri!.AbsolutePath);
            received.Add((await request.Content!.ReadFromJsonAsync<ClassificationProposalReview>())!);
            if (received.Count == 1) throw new HttpRequestException("response lost");
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(job) };
        })) { BaseAddress = new Uri("http://localhost/") };
        using var client = new ClassificationV2Client(http, _db);
        Assert.Equal(job, await client.AcceptAndMatch(expected, default));
        Assert.Equal(new[] { expected, expected }, received);
    }

    [Fact]
    public async Task BusyClassificationRetriesWithBackoffAndPreservesWritePayload()
    {
        var payloads = new List<string>();
        using var http = new HttpClient(new AsyncCallbackHandler(async request =>
        {
            payloads.Add(await request.Content!.ReadAsStringAsync());
            if (payloads.Count == 1)
            {
                var busy = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                busy.Headers.RetryAfter = new(TimeSpan.Zero); return busy;
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new ClassificationConfiguration(true, "fixture", "local", false)) };
        })) { BaseAddress = new Uri("http://localhost/") };
        using var client = new ClassificationV2Client(http, _db);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await client.Configure(new(true, "fixture", "local", false), default);
        Assert.Equal(2, payloads.Count);
        Assert.Equal(payloads[0], payloads[1]);
        Assert.True(watch.ElapsedMilliseconds >= 180);
    }

    [Fact]
    public async Task BusyReadBackoffIsCancellableWithoutRetryStorm()
    {
        var calls = 0;
        using var http = new HttpClient(new CallbackHandler(_ =>
        {
            calls++; var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(10)); return response;
        })) { BaseAddress = new Uri("http://localhost/") };
        using var client = new ClassificationV2Client(http, _db);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Configuration(cancelled.Token));
        Assert.Equal(1, calls);
    }
    [Theory]
    [InlineData("configuration", HttpStatusCode.NoContent)]
    [InlineData("jobs/latest", HttpStatusCode.NoContent)]
    [InlineData("clusters/latest", HttpStatusCode.NoContent)]
    [InlineData("configuration", HttpStatusCode.OK)]
    [InlineData("jobs/latest", HttpStatusCode.OK)]
    [InlineData("clusters/latest", HttpStatusCode.OK)]
    public async Task NoContentRepresentsUnconfiguredOrNoJob(string route, HttpStatusCode status)
    {
        using var http = new HttpClient(new Handler(status)) { BaseAddress = new Uri("http://localhost/") };
        using var client = new ClassificationV2Client(http, _db);
        object? value = route switch
        {
            "configuration" => await client.Configuration(default),
            "jobs/latest" => await client.Latest(default),
            _ => await client.LatestCluster(default)
        };
        Assert.Null(value);
    }
    [Fact]
    public async Task EmptyOptionalStreamIsAllowedButInvalidJsonAndEmptyBrowseAreNot()
    {
        using var http = new HttpClient(new CallbackHandler(_ => new(HttpStatusCode.OK)
        { Content = new StreamContent(new MemoryStream()) })) { BaseAddress = new Uri("http://localhost/") };
        using var client = new ClassificationV2Client(http, _db);
        Assert.Null(await client.Configuration(default));
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => client.Browse(new(new([], [])), default));
        using var invalidHttp = new HttpClient(new CallbackHandler(_ => new(HttpStatusCode.OK)
        { Content = new StringContent("broken-json") })) { BaseAddress = new Uri("http://localhost/") };
        using var invalid = new ClassificationV2Client(invalidHttp, _db);
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => invalid.Configuration(default));
    }
    [Fact]
    public async Task MissingScopeIsNotTreatedAsOfflineOrMissingCapability()
    {
        using var http = new HttpClient(new Handler(HttpStatusCode.Forbidden)) { BaseAddress = new Uri("http://localhost/") };
        using var client = new ClassificationV2Client(http, _db);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.Configuration(default));
    }
    [Fact]
    public async Task CacheWriteQueuesBehindMirrorWriterAndCanBeCancelledWithoutLosingRetry()
    {
        var replica = new ClassificationRepository(_db).ExportReplica();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Picer.App.Infrastructure.UiWork.RunDatabaseAsync(() =>
        {
            using var db = _db.OpenIndependentConnection();
            using var transaction = db.BeginTransaction();
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("test_writer_not_released");
            transaction.Commit();
        });
        using var http = new HttpClient(new CallbackHandler(_ =>
        { received.TrySetResult(); return new(HttpStatusCode.OK) { Content = JsonContent.Create(replica) }; })) { BaseAddress = new Uri("http://localhost/") };
        using var client = new ClassificationV2Client(http, _db);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using var stop = new CancellationTokenSource();
            var sync = client.Synchronize(stop.Token);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(100);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally { release.Set(); await writer; }
        await client.Synchronize(default);
        Assert.Equal(replica.Snapshot, new ClassificationRepository(_db).LoadReplica("http://localhost/")?.Snapshot);
    }
    [Fact]
    public async Task ReadDeadlineEndsWaitWithoutHidingCallerCancellation()
    {
        using var http = new HttpClient(new Handler(null)) { BaseAddress = new Uri("http://localhost/") };
        using var client = new ClassificationV2Client(http, _db, TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAsync<TimeoutException>(() => client.Configuration(default));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Configuration(stop.Token));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronizationDeadlinePreservesPendingWorkAndCallerCancellation(bool upload)
    {
        var repository = new ClassificationRepository(_db);
        var operation = Guid.NewGuid().ToString("N");
        if (upload) repository.QueueEdit("http://localhost/", "decisions", operation, "{}");
        using var http = new HttpClient(new Handler(null)) { BaseAddress = new Uri("http://localhost/") };
        using var client = new ClassificationV2Client(http, _db, TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAsync<TimeoutException>(() => client.Synchronize(default));
        if (upload) Assert.Equal("pending", repository.EditState("http://localhost/", operation));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Synchronize(stop.Token));
    }
    [Fact]
    public async Task ReadbackDoesNotCompleteReceiptAcknowledgedAfterRequestStarted()
    {
        const string origin = "http://localhost/";
        var repository = new ClassificationRepository(_db);
        repository.SaveReplica(origin, repository.ExportReplica());
        var earlier = Guid.NewGuid().ToString("N"); var later = Guid.NewGuid().ToString("N");
        repository.QueueEdit(origin, "decisions", earlier, "{}"); repository.MarkEdit(earlier, "acknowledged");
        using var http = new HttpClient(new CallbackHandler(_ =>
        {
            repository.QueueEdit(origin, "decisions", later, "{}"); repository.MarkEdit(later, "acknowledged");
            return new(HttpStatusCode.NoContent);
        })) { BaseAddress = new Uri(origin) };
        using var client = new ClassificationV2Client(http, _db);
        await client.Synchronize(default);
        Assert.Equal("completed", repository.EditState(origin, earlier));
        Assert.Equal("acknowledged", repository.EditState(origin, later));
    }
    [Fact]
    public async Task LateUploadReceiptDoesNotRegressAnAlreadyCompletedOperation()
    {
        const string origin = "http://localhost/";
        var repository = new ClassificationRepository(_db);
        repository.SaveReplica(origin, repository.ExportReplica());
        var operation = Guid.NewGuid().ToString("N");
        repository.QueueEdit(origin, "decisions", operation, "{}");
        using var http = new HttpClient(new CallbackHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                repository.MarkEdit(operation, "completed");
                return new(HttpStatusCode.OK);
            }
            Assert.Equal("completed", repository.EditState(origin, operation));
            return new(HttpStatusCode.NoContent);
        })) { BaseAddress = new Uri(origin) };
        using var client = new ClassificationV2Client(http, _db);
        await client.Synchronize(default);
        Assert.Equal("completed", repository.EditState(origin, operation));
    }
    [Fact]
    public async Task LateReplicaDoesNotOverwriteNewerConcurrentReadback()
    {
        const string origin = "http://localhost/";
        var repository = new ClassificationRepository(_db);
        var baseline = repository.ExportReplica();
        repository.SaveReplica(origin, baseline);
        var operation = Guid.NewGuid().ToString("N");
        repository.QueueEdit(origin, "decisions", operation, "{}"); repository.MarkEdit(operation, "acknowledged");
        var newer = baseline with { Snapshot = "newer-snapshot" };
        using var http = new HttpClient(new CallbackHandler(_ =>
        {
            repository.SaveReplica(origin, newer);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(baseline) };
        })) { BaseAddress = new Uri(origin) };
        using var client = new ClassificationV2Client(http, _db);
        await client.Synchronize(default);
        Assert.Equal(newer.Snapshot, repository.LoadReplica(origin)!.Snapshot);
        Assert.Equal("acknowledged", repository.EditState(origin, operation));
    }
    [Fact]
    public async Task SynchronizationRejectsMissingBaselineAndMissingScope()
    {
        using var empty = new HttpClient(new Handler(HttpStatusCode.NoContent)) { BaseAddress = new Uri("http://localhost/") };
        using var noBaseline = new ClassificationV2Client(empty, _db);
        await Assert.ThrowsAsync<InvalidDataException>(() => noBaseline.Synchronize(default));
        using var denied = new HttpClient(new Handler(HttpStatusCode.Forbidden)) { BaseAddress = new Uri("http://localhost/") };
        using var noScope = new ClassificationV2Client(denied, _db);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => noScope.Synchronize(default));
    }
    private sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(callback(request));
    }
    private sealed class AsyncCallbackHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => callback(request);
    }
    private sealed class Handler(HttpStatusCode? code) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (code is null) await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(code ?? HttpStatusCode.OK);
        }
    }
    public void Dispose() { _db.Dispose(); Directory.Delete(_root, true); }
}
