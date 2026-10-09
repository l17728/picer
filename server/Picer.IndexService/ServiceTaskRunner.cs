using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Picer.IndexService;

internal sealed class ServiceTaskRunner : IDisposable
{
    internal sealed record StorageOptions(long MaximumResultBytes = 64L * 1024 * 1024, long MaximumTotalBytes = 512L * 1024 * 1024,
        int MaximumTasks = 2000, int MaximumConcurrentTasks = 4, TimeSpan? ResultLifetime = null);
    private sealed record StoredRequest(string Method, string Path, string Query);
    private sealed record StoredResult(int StatusCode, string ContentType, DateTime CreatedUtc);
    private sealed record StoredFiles(DateTime CreatedUtc, long Bytes);
    private const long MetadataReservation = 32768;
    private static readonly string[] OwnedSuffixes = [".json", ".body", ".result-meta.json", ".result.json", ".tmp", ".body.part", ".json.tmp", ".result-meta.json.tmp"];
    private readonly string _directory;
    private readonly TaskJournal _journal;
    private readonly HttpClient _client;
    private readonly ConcurrentDictionary<string, string> _replayTokens = new();
    private readonly ConcurrentDictionary<string, byte> _active = new();
    private readonly ConcurrentDictionary<string, byte> _executing = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _replayCompletions = new();
    private readonly Func<Uri> _origin;
    private readonly StorageOptions _options;
    private readonly SemaphoreSlim _workers;
    private readonly object _storageGate = new();
    private sealed record RunningVideo(long VideoId, CancellationTokenSource Cancellation, TaskCompletionSource Finished);
    private readonly Dictionary<string, RunningVideo> _runningVideos = new();
    private readonly HashSet<long> _purgingVideos = [];
    private readonly ConcurrentDictionary<string, byte> _forgottenRequests = new();
    private readonly Dictionary<string, StoredFiles> _stored = new(StringComparer.Ordinal);
    private readonly Timer _cleanup;
    private long _reserved;
    private int _reservedTasks;
    private long _storedBytes;
    private bool _storageHealthy = true;

    public ServiceTaskRunner(string root, TaskJournal journal, X509Certificate2 certificate, string token, Func<Uri> origin, StorageOptions? options = null)
    {
        _directory = Path.Combine(root, "task-requests");
        Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _journal = journal; _origin = origin; _options = options ?? new();
        if (_options.MaximumResultBytes < 1 || _options.MaximumTotalBytes < _options.MaximumResultBytes + MetadataReservation || _options.MaximumTasks < 1 || _options.MaximumConcurrentTasks < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        _workers = new(_options.MaximumConcurrentTasks, _options.MaximumConcurrentTasks);
        var thumbprint = certificate.GetCertHashString();
        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.GetCertHashString() == thumbprint })
            { Timeout = TimeSpan.FromMinutes(10) };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        foreach (var group in Directory.EnumerateFiles(_directory).Select(path => new FileInfo(path))
            .Where(file => file.Name.Length > 32 && Guid.TryParseExact(file.Name[..32], "N", out _) && OwnedSuffixes.Contains(file.Name[32..]))
            .GroupBy(file => file.Name[..32]))
        {
            var entry = new StoredFiles(group.Max(file => file.LastWriteTimeUtc), group.Sum(file => file.Length));
            _stored[group.Key] = entry; _storedBytes += entry.Bytes;
        }
        lock (_storageGate) TrimStorage(0);
        _cleanup = new Timer(_ =>
        {
            try { lock (_storageGate) TrimStorage(0); }
            catch (Exception exception) when (IsStorageError(exception)) { Picer.Core.Logging.AppLogger.Warn("Task storage cleanup deferred: " + exception.GetType().Name); }
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    internal static bool CanReplay(string method, string path) => Regex.IsMatch(path,
        method == "GET" ? "^/api/service/v1/video-(probe|audio)/[1-9][0-9]*$"
        : method == "POST" ? "^/api/service/v1/video-(keyframes|transcribe|people)/[1-9][0-9]*$" : "a^", RegexOptions.CultureInvariant);

    public async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        var request = context.Request;
        if (!CanReplay(request.Method, request.Path.Value ?? "")) { await next(); return; }
        var replay = _replayTokens.TryRemove(request.Headers["X-Picer-Task-Replay"].ToString(), out var existingId);
        var id = replay ? existingId! : Guid.NewGuid().ToString("N");
        if (_forgottenRequests.ContainsKey(id)) { context.Response.StatusCode = 404; return; }
        var operation = request.Path.Value!;
        context.Response.Headers["X-Picer-Task-Id"] = id;
        if (!await _workers.WaitAsync(0, context.RequestAborted))
        {
            context.Response.StatusCode = 429;
            context.Response.Headers["Retry-After"] = "2";
            SafeAppend(id, operation, "rejected", "task_concurrency_limit");
            if (_replayCompletions.TryGetValue(id, out var rejected)) rejected.TrySetResult();
            await context.Response.WriteAsJsonAsync(new { error = "task_concurrency_limit" }, context.RequestAborted);
            return;
        }
        _executing[id] = 0;
        var originalCancellation = context.RequestAborted;
        var videoCancellation = CancellationTokenSource.CreateLinkedTokenSource(originalCancellation);
        var videoId = long.Parse(operation[(operation.LastIndexOf('/') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_storageGate)
        {
            if (_purgingVideos.Contains(videoId) || _forgottenRequests.ContainsKey(id))
            {
                _executing.TryRemove(id, out _); _workers.Release(); videoCancellation.Dispose();
                context.Response.StatusCode = 409;
                if (_replayCompletions.TryGetValue(id, out var blocked)) blocked.TrySetResult();
                return;
            }
            _runningVideos[id] = new(videoId, videoCancellation, finished);
        }
        context.RequestAborted = videoCancellation.Token;
        var originalVideoContext = TaskJournal.VideoContext.Value;
        TaskJournal.VideoContext.Value = videoId;
        var reserved = false;
        var originalBody = context.Response.Body;
        TeeCaptureStream? capture = null;
        string? storageError = "result_incomplete";
        try
        {
            SafeAppend(id, operation, "queued");
            try
            {
                lock (_storageGate)
                {
                    var reservation = _options.MaximumResultBytes + MetadataReservation;
                    TrimStorage(reservation);
                    if (_storageHealthy && _stored.Count + _reservedTasks < _options.MaximumTasks && _storedBytes + _reserved + reservation <= _options.MaximumTotalBytes)
                    { _reserved += reservation; _reservedTasks++; reserved = true; }
                }
            }
            catch (Exception exception) when (IsStorageError(exception)) { storageError = "result_storage_unavailable"; }
            if (reserved)
            {
                try
                {
                    var storedRequest = JsonSerializer.Serialize(new StoredRequest(request.Method, operation, request.QueryString.Value ?? ""));
                    if (System.Text.Encoding.UTF8.GetByteCount(storedRequest) > MetadataReservation / 2) throw new IOException("task_metadata_limit");
                    await File.WriteAllTextAsync(PathFor(id, ".json.tmp"), storedRequest, context.RequestAborted);
                    File.Move(PathFor(id, ".json.tmp"), PathFor(id, ".json"), true);
                    capture = new TeeCaptureStream(originalBody, PathFor(id, ".body.part"), _options.MaximumResultBytes);
                    context.Response.Body = capture;
                }
                catch (Exception exception) when (IsStorageError(exception)) { storageError = "result_storage_unavailable"; }
            }
            else storageError = "result_storage_quota";
            SafeAppend(id, operation, "running");
            // A known Content-Length lets clients finish reading before post-handler
            // persistence and journal finalization. Keep streaming, but let HTTP framing
            // finish only when this middleware has committed the final state.
            context.Response.OnStarting(() => { context.Response.ContentLength = null; return Task.CompletedTask; });
            await next();
            if (capture is not null)
            {
                await capture.FinishCaptureAsync();
                storageError = capture.StorageError;
                if (storageError is null)
                {
                    try
                    {
                        var metadata = new StoredResult(context.Response.StatusCode, context.Response.ContentType ?? "application/octet-stream", DateTime.UtcNow);
                        var serialized = JsonSerializer.Serialize(metadata);
                        if (System.Text.Encoding.UTF8.GetByteCount(serialized) > MetadataReservation / 2) throw new IOException("task_metadata_limit");
                        await File.WriteAllTextAsync(PathFor(id, ".result-meta.json.tmp"), serialized, CancellationToken.None);
                        File.Move(PathFor(id, ".body.part"), PathFor(id, ".body"), true);
                        File.Move(PathFor(id, ".result-meta.json.tmp"), PathFor(id, ".result-meta.json"), true);
                    }
                    catch (Exception exception) when (IsStorageError(exception)) { storageError = "result_storage_unavailable"; }
                }
            }
            SafeAppend(id, operation, context.Response.StatusCode < 400 ? "succeeded" : "failed",
                context.Response.StatusCode < 400 ? storageError : "http_" + context.Response.StatusCode);
        }
        catch (OperationCanceledException) { SafeAppend(id, operation, "canceled"); throw; }
        catch (Exception exception) { SafeAppend(id, operation, "failed", exception.GetType().Name); throw; }
        finally
        {
            try
            {
                context.Response.Body = originalBody;
                if (capture is not null) await capture.DisposeAsync();
                try
                {
                    lock (_storageGate)
                    {
                        TryDelete(PathFor(id, ".body.part")); TryDelete(PathFor(id, ".json.tmp")); TryDelete(PathFor(id, ".result-meta.json.tmp"));
                        if (storageError is not null) { TryDelete(PathFor(id, ".body")); TryDelete(PathFor(id, ".result-meta.json")); }
                        if (reserved) { _reserved -= _options.MaximumResultBytes + MetadataReservation; _reservedTasks--; }
                        RefreshStored(id);
                    }
                }
                catch (Exception exception) when (IsStorageError(exception))
                {
                    lock (_storageGate) _storageHealthy = false;
                    Picer.Core.Logging.AppLogger.Warn("Task storage inventory deferred: " + exception.GetType().Name);
                }
            }
            finally
            {
                _executing.TryRemove(id, out _); _workers.Release();
                context.RequestAborted = originalCancellation;
                TaskJournal.VideoContext.Value = originalVideoContext;
                lock (_storageGate) _runningVideos.Remove(id);
                finished.TrySetResult(); videoCancellation.Dispose();
                if (_replayCompletions.TryGetValue(id, out var completion)) completion.TrySetResult();
            }
        }
    }

    public IResult GetResult(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return Results.NotFound();
        lock (_storageGate)
        {
            TrimStorage(0);
            try
            {
                var metadata = JsonSerializer.Deserialize<StoredResult>(File.ReadAllText(PathFor(id, ".result-meta.json")));
                if (metadata is null || !File.Exists(PathFor(id, ".body"))) return Results.NotFound(new { error = "task_result_unavailable" });
                return new FileReplayResult(PathFor(id, ".body"), metadata.ContentType, metadata.StatusCode);
            }
            catch (Exception exception) when (IsStorageError(exception) || exception is JsonException)
            { return Results.NotFound(new { error = "task_result_unavailable" }); }
        }
    }

    public async Task<IResult> RetryAsync(string id, CancellationToken cancellationToken)
    {
        if (_forgottenRequests.ContainsKey(id) || !Guid.TryParseExact(id, "N", out _) || !File.Exists(PathFor(id, ".json"))) return Results.NotFound(new { error = "task_payload_unavailable" });
        if (!_active.TryAdd(id, 0)) return Results.Conflict(new { error = "task_already_running" });
        var replayToken = Guid.NewGuid().ToString("N");
        try
        {
            var stored = JsonSerializer.Deserialize<StoredRequest>(await File.ReadAllTextAsync(PathFor(id, ".json"), cancellationToken));
            if (stored is null || !CanReplay(stored.Method, stored.Path)) { CompleteRetry(id, replayToken); return Results.BadRequest(); }
            if (!_journal.RequestRetry(id)) { CompleteRetry(id, replayToken); return Results.Conflict(new { error = "task_not_retryable" }); }
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _replayCompletions[id] = completion;
            _replayTokens[replayToken] = id;
            using var request = new HttpRequestMessage(new HttpMethod(stored.Method), new Uri(_origin(), stored.Path + stored.Query));
            request.Headers.Add("X-Picer-Task-Replay", replayToken);
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.Headers.TryGetValues("X-Picer-Task-Id", out var taskIds) || !taskIds.Contains(id)) completion.TrySetResult();
            return new HttpReplayResult(response, completion.Task, () => CompleteRetry(id, replayToken));
        }
        catch (OperationCanceledException) { CompleteRetry(id, replayToken); SafeAppend(id, "retry", "canceled"); throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or SqliteException)
        { CompleteRetry(id, replayToken); SafeAppend(id, "retry", "failed", exception.GetType().Name); return Results.StatusCode(502); }
    }

    private void CompleteRetry(string id, string replayToken)
    {
        _replayTokens.TryRemove(replayToken, out _); _active.TryRemove(id, out _);
        if (_replayCompletions.TryRemove(id, out var completion)) completion.TrySetCanceled();
    }
    private string PathFor(string id, string suffix) => Path.Combine(_directory, id + suffix);
    private static bool IsStorageError(Exception exception) => exception is IOException or UnauthorizedAccessException;
    private void SafeAppend(string id, string operation, string state, string? error = null)
    {
        if (_forgottenRequests.ContainsKey(id)) return;
        try { _journal.Append(id, operation, state, error); }
        catch (Exception exception) when (IsStorageError(exception) || exception is SqliteException)
        { Picer.Core.Logging.AppLogger.Warn("Task journal unavailable: " + exception.GetType().Name); }
    }

    public async Task<bool> PurgeVideoAsync(long videoId, Func<CancellationToken, Task<bool>> purge, CancellationToken token)
    {
        RunningVideo[] running;
        lock (_storageGate)
        {
            if (!_purgingVideos.Add(videoId)) throw new InvalidOperationException("video_purge_in_progress");
            running = _runningVideos.Values.Where(value => value.VideoId == videoId).ToArray();
        }
        try
        {
            foreach (var operation in running) try { operation.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
            await Task.WhenAll(running.Select(operation => operation.Finished.Task)).WaitAsync(token);
            lock (_storageGate)
            {
                var ids = _journal.GetVideoRequestIds(videoId).ToHashSet(StringComparer.Ordinal);
                foreach (var id in _stored.Keys.ToArray())
                {
                    try
                    {
                        var request = JsonSerializer.Deserialize<StoredRequest>(File.ReadAllText(PathFor(id, ".json")));
                        if (request is not null && CanReplay(request.Method, request.Path) && request.Path.EndsWith("/" + videoId, StringComparison.Ordinal)) ids.Add(id);
                    }
                    catch (FileNotFoundException) { }
                    catch (JsonException) { }
                }
                foreach (var id in ids)
                {
                    _forgottenRequests[id] = 0;
                    foreach (var suffix in OwnedSuffixes) File.Delete(PathFor(id, suffix));
                    RefreshStored(id);
                }
                _journal.ForgetRequests(ids);
            }
            return await purge(token);
        }
        finally { lock (_storageGate) _purgingVideos.Remove(videoId); }
    }
    private void RefreshStored(string id)
    {
        if (_stored.Remove(id, out var old)) _storedBytes -= old.Bytes;
        var files = OwnedSuffixes.Select(suffix => new FileInfo(PathFor(id, suffix))).Where(file => file.Exists).ToArray();
        if (files.Length == 0) return;
        var entry = new StoredFiles(files.Max(file => file.LastWriteTimeUtc), files.Sum(file => file.Length));
        _stored[id] = entry; _storedBytes += entry.Bytes;
    }
    private void TrimStorage(long incoming)
    {
        var cutoff = DateTime.UtcNow - (_options.ResultLifetime ?? TimeSpan.FromDays(7));
        foreach (var pair in _stored.OrderBy(pair => pair.Value.CreatedUtc).ToArray())
        {
            if (_executing.ContainsKey(pair.Key) || _active.ContainsKey(pair.Key)) continue;
            if (pair.Value.CreatedUtc >= cutoff && _storedBytes + _reserved + incoming <= _options.MaximumTotalBytes && _stored.Count < _options.MaximumTasks) continue;
            foreach (var suffix in OwnedSuffixes) TryDelete(PathFor(pair.Key, suffix));
            RefreshStored(pair.Key);
        }
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private sealed record FileReplayResult(string Path, string ContentType, int Status) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            FileStream source;
            try { source = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan); }
            catch (FileNotFoundException) { context.Response.StatusCode = 410; return; }
            await using (source)
            {
                context.Response.StatusCode = Status; context.Response.ContentType = ContentType; context.Response.ContentLength = source.Length;
                await source.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
        }
    }
    private sealed record HttpReplayResult(HttpResponseMessage Response, Task Finalized, Action Completed) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            try
            {
                using (Response)
                {
                    context.Response.StatusCode = (int)Response.StatusCode;
                    context.Response.ContentType = Response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
                    context.Response.ContentLength = null;
                    await Response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
                    await Finalized.WaitAsync(TimeSpan.FromSeconds(30), context.RequestAborted);
                }
            }
            finally { Completed(); }
        }
    }

    private sealed class TeeCaptureStream(Stream output, string path, long limit) : Stream
    {
        private FileStream? _capture = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        private long _length;
        public string? StorageError { get; private set; }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { output.Flush(); try { _capture?.Flush(); } catch (Exception exception) when (IsStorageError(exception)) { Disable("result_storage_unavailable"); } }
        public override async Task FlushAsync(CancellationToken cancellationToken) { await output.FlushAsync(cancellationToken); if (_capture is not null) try { await _capture.FlushAsync(cancellationToken); } catch (Exception exception) when (IsStorageError(exception)) { Disable("result_storage_unavailable"); } }
        public override void Write(byte[] buffer, int offset, int count)
        {
            output.Write(buffer, offset, count);
            if (_capture is null) return;
            if (_length + count > limit) { Disable("result_storage_item_limit"); return; }
            try { _capture.Write(buffer, offset, count); _length += count; } catch (Exception exception) when (IsStorageError(exception)) { Disable("result_storage_unavailable"); }
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await output.WriteAsync(buffer, cancellationToken);
            if (_capture is null) return;
            if (_length + buffer.Length > limit) { Disable("result_storage_item_limit"); return; }
            try { await _capture.WriteAsync(buffer, cancellationToken); _length += buffer.Length; } catch (Exception exception) when (IsStorageError(exception)) { Disable("result_storage_unavailable"); }
        }
        private void Disable(string error) { StorageError = error; try { _capture?.Dispose(); } catch (IOException) { } _capture = null; TryDelete(path); }
        public async Task FinishCaptureAsync()
        {
            if (_capture is null) return;
            try { await _capture.DisposeAsync(); _capture = null; } catch (Exception exception) when (IsStorageError(exception)) { Disable("result_storage_unavailable"); }
        }
        public override async ValueTask DisposeAsync() { await FinishCaptureAsync(); GC.SuppressFinalize(this); }
        protected override void Dispose(bool disposing) { if (disposing) { try { _capture?.Dispose(); } catch (IOException) { } _capture = null; } base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        _cleanup.Dispose(); _client.Dispose();
        foreach (var completion in _replayCompletions.Values) completion.TrySetCanceled();
    }
}
