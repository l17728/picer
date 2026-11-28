using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using Picer.Core.Classification;
using Picer.Core.Gateway;
using Picer.Core.Imaging;

namespace Picer.App;

internal sealed class ClassificationV2Client : IDisposable
{
    private static readonly System.Text.Json.JsonSerializerOptions ResponseJson = new(System.Text.Json.JsonSerializerDefaults.Web);
    private readonly HttpClient? _client;
    private readonly Func<Picer.Core.Data.PicerDatabase> _database;
    private readonly TimeSpan _readTimeout = TimeSpan.FromSeconds(20);
    private static readonly object WorkerLock = new();
    private static Task? _worker;
    public bool LastEditPending { get; private set; }
    public ClassificationV2Client()
    {
        _database = () => AppServices.Db;
        if (AppServices.ServiceMode)
        {
            _client = AppServices.CreateClassificationHttpClient();
            _client.BaseAddress = new Uri(_client.BaseAddress!, "v2/");
        }
    }
    internal ClassificationV2Client(HttpClient client, Picer.Core.Data.PicerDatabase database, TimeSpan? readTimeout = null)
    { _client = client; _database = () => database; _readTimeout = readTimeout ?? TimeSpan.FromSeconds(20); }
    public async Task<ClassificationBrowseResult> Browse(ClassificationBrowseRequest request, CancellationToken token)
    {
        try { return await Call("browse", request, repo => repo.Browse(request), false, token); }
        catch (HttpRequestException exception) when (OfflineAllowed(exception) && _client is not null)
        {
            var replica = await Cached(token) ?? throw new InvalidOperationException("服务不可达，尚无分类离线快照。请联网后刷新。");
            return ClassificationRepository.BrowseReplica(replica, request);
        }
    }
    public async Task Synchronize(CancellationToken token)
    {
        if (_client is null)
        {
            if (AppServices.LocalSpaceSyncConfigured) await AppServices.SyncLocalChangesToRemoteAsync(token);
            return;
        }
        var origin = _client.BaseAddress!.AbsoluteUri;
        await foreach (var edit in ReadOutbox(origin, token))
        {
            if (edit.State != "pending") continue;
            using var request = new HttpRequestMessage(HttpMethod.Post, edit.Route)
                { Content = new StringContent(edit.Payload, Encoding.UTF8, "application/json") };
            using var sent = await SendSynchronization(request, token);
            ThrowIfUnavailable(sent);
            var state = sent.StatusCode == System.Net.HttpStatusCode.Conflict ? "conflict" : "acknowledged";
            if (state != "conflict") sent.EnsureSuccessStatusCode();
            await Infrastructure.UiWork.RunDatabaseAsync("Classification.MarkEdit", () => { using var db = _database().OpenIndependentConnection(); new ClassificationRepository(db).MarkEdit(edit.OperationId, state, "pending"); }, token);
        }
        var baseline = await Task.Run(() =>
        {
            using var read = _database().OpenReadOnlyConnection();
            var repository = new ClassificationRepository(read);
            // Only receipts observed before this read request can be confirmed by
            // its snapshot. Another window may acknowledge newer edits in flight.
            return (Snapshot: repository.LoadReplica(origin)?.Snapshot,
                Acknowledged: repository.PendingSnapshot(origin).Where(edit => edit.State == "acknowledged")
                    .Select(edit => edit.OperationId).ToArray());
        }, token);
        using var readRequest = new HttpRequestMessage(HttpMethod.Get, "replica" + (baseline.Snapshot is null ? "" : "?knownSnapshot=" + Uri.EscapeDataString(baseline.Snapshot)));
        using var response = await SendSynchronization(readRequest, token);
        ThrowIfUnavailable(response);
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            if (baseline.Snapshot is null) throw new InvalidDataException("classification_replica_missing_baseline");
            await CompleteAcknowledged(origin, baseline.Acknowledged, token); return;
        }
        response.EnsureSuccessStatusCode();
        var replica = await response.Content.ReadFromJsonAsync<ClassificationReplica>(cancellationToken: token)
            ?? throw new InvalidDataException("classification_replica_invalid");
        var saved = await Infrastructure.UiWork.RunDatabaseAsync("Classification.SaveReplica", () =>
        {
            using var db = _database().OpenIndependentConnection();
            var repository = new ClassificationRepository(db);
            if (!repository.TrySaveReplica(origin, replica, baseline.Snapshot)) return false;
            if (repository.LoadReplica(origin)?.Snapshot != replica.Snapshot) throw new InvalidDataException("classification_replica_readback_failed");
            return true;
        }, token);
        if (saved) await CompleteAcknowledged(origin, baseline.Acknowledged, token);
    }
    public Task<ClassificationConfiguration?> Configuration(CancellationToken token) =>
        Call<ClassificationConfiguration?>("configuration", null, repo => repo.Configuration(), false, token);
    public async Task<string> Epoch(CancellationToken token)
    {
        try { return await Call("epoch", null, repo => repo.Epoch, false, token); }
        catch (HttpRequestException exception) when (OfflineAllowed(exception) && _client is not null)
        { return (await Cached(token))?.Epoch ?? throw new InvalidOperationException("没有可用的离线分类快照"); }
    }
    public async Task<int> Decide(ClassificationDecision decision, CancellationToken token)
    {
        if (_client is null) return await Call("decisions", decision, repo => repo.Decide(decision), true, token);
        await Queue("decisions", decision.OperationId, decision, token);
        try
        {
            await Synchronize(token);
            var state = await EditState(decision.OperationId, token);
            if (state == "conflict") throw new InvalidOperationException("人工决定与当前来源或内容冲突，已保留记录。请刷新照片后重新审核，未覆盖服务端决定。");
            return state == "completed" ? decision.ContentIdentities.Count : 0;
        }
        catch (HttpRequestException exception) when (OfflineAllowed(exception)) { return 0; }
    }
    public async Task Cluster(IReadOnlyList<long> ids, CancellationToken token)
    {
        var request = new ClassificationClusterRequest(Guid.NewGuid().ToString("N"), ids);
        if (_client is null)
        {
            await Infrastructure.UiWork.RunDatabaseAsync("Classification.PlanCluster", () => { using var db = _database().OpenIndependentConnection(); new ClassificationRepository(db).PlanCluster(request); }, token);
            StartLocalWorker();
            return;
        }
        using var response = await SendWithBackoff(() => new HttpRequestMessage(HttpMethod.Post, "clusters") { Content = JsonContent.Create(request) }, token);
        ThrowIfUnavailable(response);
        response.EnsureSuccessStatusCode();
    }
    public Task<ClassificationConfiguration> Configure(ClassificationConfiguration config, CancellationToken token) =>
        Call("configuration", config, repo => { repo.Configure(config); return config; }, true, token);
    public Task<ClassificationPlanPreview> Plan(CancellationToken token, bool retryFailures = false)
    {
        var request = Guid.NewGuid().ToString("N");
        return Call("incremental-plans", new { RequestId = request, RetryFailures = retryFailures }, repo => ClassificationPlanPreview.From(repo.Plan(request,
            repo.Configuration() ?? throw new InvalidOperationException("请先保存分类模型配置"), retryFailures)), true, token);
    }
    public async Task<ClassificationJob> Start(string plan, CancellationToken token)
    {
        var request = Guid.NewGuid().ToString("N");
        var job = await Call("jobs", new { PlanId = plan, RequestId = request }, repo => repo.Start(plan, request), true, token);
        if (_client is null) StartLocalWorker();
        return job;
    }
    public async Task<ClassificationJob?> Latest(CancellationToken token)
    {
        var job = await Call<ClassificationJob?>("jobs/latest", null, repo => repo.LatestJob(), false, token);
        if (_client is null && job?.State is "queued" or "running") StartLocalWorker();
        return job;
    }
    public Task<ClassificationJob> Job(string id, CancellationToken token) =>
        Call("jobs/" + id, null, repo => repo.Job(id), false, token);

    public async Task<ClassificationJob> AcceptAndMatch(ClassificationProposalReview review, CancellationToken token)
    {
        Task<ClassificationJob> Submit() => Call("proposals/accept-and-match", review, repo => repo.AcceptAndMatch(review), true, token);
        ClassificationJob job;
        try { job = await Submit(); }
        catch (HttpRequestException error) when (_client is not null && error.StatusCode is null && !token.IsCancellationRequested)
        {
            // The service may already have committed the job before the response was lost.
            job = await Submit();
        }
        if (_client is null) StartLocalWorker();
        return job;
    }
    public Task<ClassificationJob> Control(string id, string operation, CancellationToken token) =>
        Call($"jobs/{id}/{operation}", new { }, repo => { repo.Control(id, operation); return repo.Job(id); }, true, token);
    public async Task<IReadOnlyList<ClassificationProposal>> Proposals(CancellationToken token)
    {
        try { return await Call<IReadOnlyList<ClassificationProposal>>("proposals", null, repo => repo.Proposals(), false, token); }
        catch (HttpRequestException exception) when (_client is not null && OfflineAllowed(exception))
        { return (await Cached(token))?.Proposals ?? throw new InvalidOperationException("没有可用的离线建议快照"); }
    }
    public Task<ClassificationClusterStatus?> LatestCluster(CancellationToken token) =>
        Call<ClassificationClusterStatus?>("clusters/latest", null, repo => repo.LatestCluster(), false, token);
    public Task<ClassificationTaxonomySnapshot> Review(ClassificationProposalReview review, CancellationToken token) =>
        Edit("proposals/review", review.OperationId, review, repo => repo.Review(review), token);
    public Task<ClassificationTaxonomySnapshot> EditTaxonomy(ClassificationTaxonomyEdit edit, CancellationToken token) =>
        Edit("taxonomy/edit", edit.OperationId, edit, repo => repo.EditTaxonomy(edit), token);

    private async Task<ClassificationTaxonomySnapshot> Edit<T>(string route, string id, T request,
        Func<ClassificationRepository, ClassificationTaxonomySnapshot> local, CancellationToken token)
    {
        LastEditPending = false;
        if (_client is null) return await Call(route, request, local, true, token);
        var baseline = await Cached(token) ?? throw new InvalidOperationException("请先联网刷新分类快照，再修改分类结构。");
        await Queue(route, id, request, token);
        try
        {
            await Synchronize(token);
            var state = await EditState(id, token);
            if (state == "conflict") throw new InvalidOperationException("分类结构已经变化，操作已保留为冲突。请刷新并重新审核。");
            LastEditPending = state != "completed";
            return (await Cached(token))?.Taxonomy ?? baseline.Taxonomy;
        }
        catch (HttpRequestException exception) when (OfflineAllowed(exception))
        { LastEditPending = true; return baseline.Taxonomy; }
    }

    private async Task<T> Call<T>(string route, object? body, Func<ClassificationRepository, T> local, bool write, CancellationToken token)
    {
        if (_client is null)
        {
            T Local()
            {
                using var db = write ? _database().OpenIndependentConnection() : _database().OpenReadOnlyConnection();
                return local(new ClassificationRepository(db));
            }
            return write ? await Infrastructure.UiWork.RunDatabaseAsync("Classification.LocalWrite", Local, token)
                : await Task.Run(Local, token);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (!write) deadline.CancelAfter(_readTimeout);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await SendWithBackoff(() => new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, route)
                { Content = body is null ? null : JsonContent.Create(body) }, deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Picer.Core.Logging.AnalysisDiagnosticLog.Warn($"Classification read timeout; elapsedMs={watch.ElapsedMilliseconds}");
            throw new TimeoutException("分类读取超时，请重试；已有结果保留。");
        }
        using var responseLease = response;
        Picer.Core.Logging.AnalysisDiagnosticLog.Info($"Classification request completed; route={route}; status={(int)response.StatusCode}; elapsedMs={watch.ElapsedMilliseconds}");
        ThrowIfUnavailable(response);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            throw new InvalidOperationException("计划、快照或配置已经变化，请刷新后重试。");
        response.EnsureSuccessStatusCode();
        if (route is "configuration" or "jobs/latest" or "clusters/latest")
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return default!;
            // ASP.NET Results.Ok(null) returns 200 with an empty body. These
            // three resources are optional; a fresh space may have none yet.
            var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token);
            if (bytes.Length == 0) return default!;
            return System.Text.Json.JsonSerializer.Deserialize<T>(bytes, ResponseJson)!;
        }
        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: token))!;
    }
    private static bool OfflineAllowed(HttpRequestException exception) => exception.StatusCode is null or System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.GatewayTimeout or System.Net.HttpStatusCode.TooManyRequests;
    private static void ThrowIfUnavailable(HttpResponseMessage response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("分类服务暂忙，已等待后重试；已有结果保留，请稍后刷新。", null, response.StatusCode);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("当前索引服务尚未支持分类 V2；可使用旧分类视图，待统一部署后再启用新功能。");
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("当前设备缺少此项分类授权。新分类结构权限需在设备授权中启用；旧授权不会自动扩权。");
    }
    private async Task<HttpResponseMessage> SendSynchronization(HttpRequestMessage request, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_readTimeout);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var payload = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(deadline.Token);
            var response = await SendWithBackoff(() =>
            {
                var copy = new HttpRequestMessage(request.Method, request.RequestUri);
                foreach (var header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
                if (payload is not null)
                {
                    copy.Content = new ByteArrayContent(payload);
                    foreach (var header in request.Content!.Headers) copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                return copy;
            }, deadline.Token);
            Picer.Core.Logging.AnalysisDiagnosticLog.Info($"Classification synchronization response; status={(int)response.StatusCode}; elapsedMs={watch.ElapsedMilliseconds}");
            return response;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Picer.Core.Logging.AnalysisDiagnosticLog.Warn($"Classification synchronization timeout; elapsedMs={watch.ElapsedMilliseconds}");
            throw new TimeoutException("分类同步超时，已有结果和待同步操作已保留，请重试。");
        }
    }
    private async Task<HttpResponseMessage> SendWithBackoff(Func<HttpRequestMessage> create, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = create();
            var response = await _client!.SendAsync(request, token);
            if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests || attempt >= 2) return response;
            var retry = response.Headers.RetryAfter;
            var delay = retry?.Delta ?? (retry?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(2 << attempt));
            if (delay > TimeSpan.FromSeconds(30)) return response;
            if (delay < TimeSpan.FromMilliseconds(200)) delay = TimeSpan.FromMilliseconds(200);
            response.Dispose();
            Picer.Core.Logging.AnalysisDiagnosticLog.Info($"Classification busy; retry={attempt + 1}; delayMs={delay.TotalMilliseconds:0}");
            await Task.Delay(delay, token);
        }
    }
    private Task<string?> EditState(string id, CancellationToken token) => Task.Run(() =>
    { using var db = _database().OpenReadOnlyConnection(); return new ClassificationRepository(db).EditState(_client!.BaseAddress!.AbsoluteUri, id); }, token);
    private Task<ClassificationReplica?> Cached(CancellationToken token) => Task.Run(() =>
    { using var db = _database().OpenReadOnlyConnection(); return new ClassificationRepository(db).LoadReplica(_client!.BaseAddress!.AbsoluteUri); }, token);
    public async Task<ClassificationReplica?> RetrievalEvidence(CancellationToken token)
    {
        if (_client is null) return await Task.Run(() =>
        { using var db = _database().OpenReadOnlyConnection(); return new ClassificationRepository(db).ExportReplica(); }, token);
        try { await Synchronize(token); return await Cached(token); }
        catch (HttpRequestException exception) when (OfflineAllowed(exception))
        {
            // Without a fresh server fingerprint, cached machine suggestions cannot prove current input validity.
            var cached = await Cached(token);
            return cached is null ? null : cached with { Photos = cached.Photos.Select(photo => photo with { SuggestedIds = [], LegacySuggestedIds = [], LegacyIds = [] }).ToArray() };
        }
    }
    private Task Queue<T>(string route, string id, T payload, CancellationToken token) => Infrastructure.UiWork.RunDatabaseAsync("Classification.QueueEdit", () =>
    { using var db = _database().OpenIndependentConnection(); new ClassificationRepository(db).QueueEdit(_client!.BaseAddress!.AbsoluteUri, route, id, System.Text.Json.JsonSerializer.Serialize(payload)); }, token);
    private Task CompleteAcknowledged(string origin, IReadOnlyList<string> operations, CancellationToken token) => Infrastructure.UiWork.RunDatabaseAsync("Classification.CompleteAcknowledged", () =>
    {
        using var db = _database().OpenIndependentConnection(); var repository = new ClassificationRepository(db);
        repository.CompleteAcknowledgedEdits(origin, operations);
    }, token);

    private async IAsyncEnumerable<ClassificationOutboxItem> ReadOutbox(string origin,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var high = await Task.Run(() => { using var read = _database().OpenReadOnlyConnection(); return new ClassificationRepository(read).OutboxHighWater(origin); }, token);
        long after = 0;
        while (true)
        {
            var page = await Task.Run(() => { using var read = _database().OpenReadOnlyConnection(); return new ClassificationRepository(read).Outbox(origin, after, high); }, token);
            if (page.Count == 0) yield break;
            after = page[^1].Sequence;
            foreach (var edit in page) { token.ThrowIfCancellationRequested(); yield return edit; }
        }
    }

    private static void StartLocalWorker()
    {
        lock (WorkerLock)
        {
            if (_worker is { IsCompleted: false }) return;
            _worker = Task.Run(async () =>
            {
                var database = AppServices.Db;
                using var localTransport = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                var model = new ClassificationModel(async (image, token) =>
                {
                    if (AppServices.Gateway is not IMediaAssetGateway media) throw new IOException("classification_media_unavailable");
                    await using var stream = await media.OpenPhotoAnalysisInputAsync(image, token)
                        ?? await media.OpenOriginalImageAsync(image.Id, cancellationToken: token)
                        ?? throw new IOException("classification_media_unavailable");
                    using var raster = await RasterImage.LoadAsync(stream, token);
                    var scale = Math.Min(1, 1280d / Math.Max(raster.Width, raster.Height));
                    if (scale < 1) raster.Resize(Math.Max(1, (int)(raster.Width * scale)), Math.Max(1, (int)(raster.Height * scale)));
                    raster.StripMetadata();
                    using var output = new MemoryStream();
                    await raster.SaveAsJpegAsync(output, 82, token);
                    return output.ToArray();
                }, async (configuration, payload, token) =>
                {
                    using var serviceTransport = configuration.Endpoint == "local" ? null : AppServices.CreateClassificationHttpClient();
                    Uri endpoint;
                    if (configuration.Endpoint == "local")
                    {
                        var configured = Environment.GetEnvironmentVariable("PICER_LOCAL_MODEL_ENDPOINT") ?? "http://127.0.0.1:8090/api/vl/chat";
                        if (!Uri.TryCreate(configured, UriKind.Absolute, out var localEndpoint) || !localEndpoint.IsLoopback || localEndpoint.Scheme is not ("http" or "https"))
                            throw new InvalidOperationException("classification_local_endpoint_invalid");
                        endpoint = localEndpoint;
                    }
                    else endpoint = ClassificationModel.ServiceRelayEndpoint(serviceTransport!.BaseAddress!, configuration.ModelAgentId);
                    using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(payload) };
                    message.Content.Headers.ContentType = new("application/json");
                    using var response = await (serviceTransport ?? localTransport).SendAsync(message, token);
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync(token);
                });
                var worker = new ClassificationWorker(database.OpenIndependentConnection, model);
                while (ReferenceEquals(database, AppServices.Db))
                {
                    try { if (await worker.ProcessOneAsync(CancellationToken.None)) continue; }
                    catch (Exception exception) { Picer.Core.Logging.AppLogger.Warn($"Local classification deferred; type={exception.GetType().Name}"); }
                    await Task.Delay(2000);
                }
            });
        }
    }
    public void Dispose() => _client?.Dispose();
}
