using Picer.Core.Gateway;
using System.Net.WebSockets;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using System.Windows.Forms;

namespace Picer.MediaAgent;

internal sealed record FaceAdapterResult(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("modelVersion")] string ModelVersion,
    [property: JsonPropertyName("faces")] IReadOnlyList<FaceAdapterFace> Faces);

internal sealed record FaceAdapterFace(
    [property: JsonPropertyName("ordinal")] int Ordinal,
    [property: JsonPropertyName("x")] float X,
    [property: JsonPropertyName("y")] float Y,
    [property: JsonPropertyName("width")] float Width,
    [property: JsonPropertyName("height")] float Height,
    [property: JsonPropertyName("confidence")] float Confidence,
    [property: JsonPropertyName("embedding")] float[]? Embedding);

internal static class Program
{
    private const string SingleInstanceMutexName = "Global\\Picer.MediaAgent.Singleton";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, SingleInstanceMutexName, out var acquired);
        if (!acquired) return;
        ApplicationConfiguration.Initialize();
        using var agent = new AgentApplication();
        Application.Run();
    }
}

internal sealed partial class AgentApplication : IDisposable
{
    private readonly Picer.Core.Indexing.DirectoryMediaScanner _directoryScanner;
    private const long MaxAssetBytes = 64L * 1024 * 1024;
    private const int MaxTaskPayloadChars = 16 * 1024 * 1024;
    private readonly NotifyIcon _icon;
    private CancellationTokenSource _stop = new();
    private bool _changingConnection;
    private bool _settingsOpen;
    private bool _disposed;
    private MediaAgentSettings? _settings;
    private string[] _roots = [];
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _aiRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _taskRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _runtimeConfig = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _modelClient = new() { Timeout = TimeSpan.FromMinutes(10) };
    private Task? _loop;
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "picer-media-agent.log");
    private static readonly string[] SupportedVideoFaceModelPriority =
        ["insightface-arcface", "adaface", "arcface-paddle", "facex-zoo", "arcface", "sface"];

    private sealed record VideoFaceModelAttempt(string ModelVersion, List<object> Faces, int FrameCount,
        int FailedFrames, bool Unsupported, bool Fatal = false);

    public AgentApplication()
    {
        _directoryScanner = new((path, video, token) => Picer.Core.Indexing.DirectoryMediaMetadataReader.ReadAsync(path, video, token, Setting("PICER_FFMPEG_PATH")));
        var menu = new ContextMenuStrip();
        menu.Items.Add("代理设置", null, async (_, _) => await ConfigureAsync());
        menu.Items.Add("重新连接", null, async (_, _) => await ReconnectAsync());
        menu.Items.Add("退出", null, (_, _) => Application.Exit());
        _icon = new NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Visible = true, Text = "Picer 原图代理", ContextMenuStrip = menu };
        _icon.DoubleClick += async (_, _) => await ConfigureAsync();
        try
        {
            _settings = MediaAgentSettings.FromEnvironment(Environment.GetEnvironmentVariable) ?? AgentConfiguration.Load();
            if (_settings is not null) AgentConfiguration.Save(_settings);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or IOException or CryptographicException or JsonException or UnauthorizedAccessException)
        { Log("agent settings require repair; type=" + exception.GetType().Name); }
        // Defer first-run UI until the Windows Forms message loop is active.
        var startup = new System.Windows.Forms.Timer { Interval = 100 };
        startup.Tick += async (_, _) =>
        {
            startup.Stop(); startup.Dispose();
            if (_settings is null) await ConfigureAsync(); else await ReconnectAsync();
        };
        startup.Start();
    }

    private async Task ConfigureAsync()
    {
        if (_changingConnection || _settingsOpen || _disposed) return;
        _settingsOpen = true;
        try
        {
            using var form = new AgentSettingsForm(_settings);
            if (form.ShowDialog() == DialogResult.OK && form.Settings is { } settings)
                await ReconnectAsync(settings);
        }
        finally { _settingsOpen = false; }
    }

    private async Task ReconnectAsync(MediaAgentSettings? replacement = null)
    {
        if (_changingConnection || _disposed) return;
        if (replacement is null && _settings is null) { await ConfigureAsync(); return; }
        _changingConnection = true;
        try
        {
            _icon.Text = "Picer 原图代理（正在重新连接）";
            await _stop.CancelAsync();
            if (_loop is not null) { try { await _loop; } catch (OperationCanceledException) { } }
            _stop.Dispose();
            _stop = new CancellationTokenSource();
            _settings = replacement ?? _settings;
            _roots = _settings!.Roots;
            _runtimeConfig.Clear();
            var token = _stop.Token;
            _loop = Task.Run(() => RunAsync(token));
        }
        catch (Exception exception) { Log("reconnect failed; type=" + exception.GetType().Name); _icon.Text = "Picer 原图代理（连接失败）"; }
        finally { _changingConnection = false; }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var settings = _settings!.Validate();
        var pin = Convert.FromBase64String(settings.SpkiPin);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Log($"connecting; rootCount={settings.Roots.Length}");
                using var socket = new ClientWebSocket();
                socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) => MediaAgentSettings.MatchesCertificate(certificate, pin);
                socket.Options.SetRequestHeader("Authorization", "Bearer " + settings.Token);
                var endpoint = new UriBuilder(settings.ServiceUrl) { Scheme = "wss", Path = "/api/agent/v1/connect" };
                await socket.ConnectAsync(endpoint.Uri, cancellationToken);
                Log($"connected; socketState={socket.State}; processId={Environment.ProcessId}");
                _mediaPreviews.Clear();
                _mediaLeases.Clear();
                _icon.Text = "Picer 原图代理（已连接）";
                await SendAsync(socket, new
                {
                    type = "agent.register",
                    agentId = Environment.MachineName,
                    // Only advertise handlers that are implemented. Media
                    // task capabilities are added when their task protocol is
                    // deployed; never claim readiness from configuration alone.
                    capabilities = BuildCapabilities()
                }, cancellationToken);
                using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var requests = new List<Task>();
                var receive = ReceiveAsync(socket, requests, connection.Token);
                var heartbeat = HeartbeatAsync(socket, connection.Token);
                await Task.WhenAny(receive, heartbeat);
                await connection.CancelAsync();
                socket.Abort();
                try { await Task.WhenAll(receive, heartbeat); }
                finally
                {
                    try { await Task.WhenAll(requests); }
                    catch (Exception exception) { Log("connection requests finished; type=" + exception.GetType().Name); }
                    foreach (var requestId in _taskRequests.Keys)
                        if (_taskRequests.TryRemove(requestId, out var registration)) registration.Dispose();
                }

            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                Log($"connection failed: {exception.GetType().Name}");
                _icon.Text = "Picer 原图代理（未连接）";
                try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private static void Log(string message)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.UtcNow:O} {message}{Environment.NewLine}"); } catch { }
    }

    private async Task HeartbeatAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                await SendAsync(socket, new { type = "agent.heartbeat", utc = DateTime.UtcNow }, cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            Log($"heartbeat failed; socketState={socket.State}; exception={exception.GetType().Name}; inner={(exception.InnerException?.GetType().Name ?? "none")}");
            throw;
        }
    }

    private async Task ReceiveAsync(ClientWebSocket socket, List<Task> requests, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var message = await ReceiveTextAsync(socket, buffer, cancellationToken);
            if (message is null) return;
            using var document = JsonDocument.Parse(message.Value);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type)) continue;
            switch (type.GetString())
            {
                case "media.cache.clear":
                    _mediaPreviews.Clear();
                    _mediaLeases.Clear();
                    break;
                case "media.release":
                    _mediaLeases.Release(root.GetProperty("etag").GetString()!);
                    break;
                case "media.keepalive":
                    _mediaLeases.Touch(root.GetProperty("etag").GetString()!);
                    break;
                case "media.open":
                    requests.RemoveAll(task => task.IsCompleted);
                    requests.Add(ServeMediaAsync(root.Clone(), cancellationToken));
                    break;
                case "asset.open":
                    requests.RemoveAll(task => task.IsCompleted);
                    requests.Add(ServeAssetAsync(socket, root, cancellationToken));
                    break;
                case "ai.open":
                    requests.RemoveAll(task => task.IsCompleted);
                    requests.Add(ServeAiAsync(socket, root, cancellationToken));
                    break;
                case "task.open":
                    if (root.TryGetProperty("requestId", out var taskId))
                    {
                        var requestId = taskId.GetString() ?? "";
                        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        if (_taskRequests.TryAdd(requestId, linked))
                        {
                            requests.RemoveAll(task => task.IsCompleted);
                            requests.Add(ServeTaskAsync(socket, root, linked.Token));
                        }
                        else linked.Dispose();
                    }
                    break;
                case "task.cancel":
                    if (root.TryGetProperty("requestId", out var cancelTaskId) && _taskRequests.TryGetValue(cancelTaskId.GetString() ?? "", out var taskRequest))
                        taskRequest.Cancel();
                    break;
                case "agent.config":
                    if (root.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object)
                        foreach (var property in config.EnumerateObject())
                            if (!property.Name.Contains("key", StringComparison.OrdinalIgnoreCase) && !property.Name.Contains("secret", StringComparison.OrdinalIgnoreCase) && !property.Name.Contains("token", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                                _runtimeConfig[property.Name] = property.Value.GetString() ?? "";
                    Log("agent config applied; keys=" + string.Join(',', _runtimeConfig.Keys));
                    await SendAsync(socket, new { type = "agent.capabilities", capabilities = BuildCapabilities() }, cancellationToken);
                    break;
                case "ai.cancel":
                    if (root.TryGetProperty("requestId", out var cancelId) && _aiRequests.TryGetValue(cancelId.GetString() ?? "", out var request)) request.Cancel();
                    break;
            }
        }
    }

    private string[] BuildCapabilities()
    {
        var capabilities = new List<string> { "originalRead", "videoRead", "range", "ai.proxy", "directory.media", Picer.Core.Gateway.MediaWire.Capability, Picer.Core.Gateway.MediaWire.ReuseCapability, Picer.Core.Gateway.MediaWire.AnalysisInputCapability };
        capabilities.Add(Picer.Core.Indexing.MediaConsistencyVerifier.Capability);
        var ffmpeg = Setting("PICER_FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(ffmpeg) && File.Exists(ffmpeg) && File.Exists(Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe")))
        {
            capabilities.Add("ffmpeg.probe");
            capabilities.Add("ffmpeg.keyframes");
            capabilities.Add("ffmpeg.audio_extract");
        }
            if (Uri.TryCreate(Setting("PICER_LOCAL_ASR_ENDPOINT"), UriKind.Absolute, out _))
                capabilities.AddRange(["asr.transcribe", "asr.stream"]);
            if (Uri.TryCreate(Setting("PICER_LOCAL_TTS_ENDPOINT"), UriKind.Absolute, out _))
                capabilities.Add("tts.synthesize");
            if (Uri.TryCreate(Setting("PICER_LOCAL_VL_ENDPOINT"), UriKind.Absolute, out _))
                capabilities.Add("vl.analyze");
            if (Uri.TryCreate(Setting("PICER_LOCAL_FACE_ENDPOINT"), UriKind.Absolute, out _))
                capabilities.AddRange(["face.detect", "face.embed", "video.people"]);
            if (TrainingEndpoint() is not null) capabilities.Add("training.proxy");
        return capabilities.ToArray();
    }

    private Uri? TrainingEndpoint()
    {
        var configured = Setting("PICER_LOCAL_TRAINING_ENDPOINT");
        if (Uri.TryCreate(configured, UriKind.Absolute, out var training) && training.IsLoopback
            && training.Scheme is "http" or "https") return training;
        // The bundled ModelServer hosts both inference and training on this origin.
        if (Uri.TryCreate(Setting("PICER_LOCAL_FACE_ENDPOINT"), UriKind.Absolute, out var face)
            && face.IsLoopback && face.AbsolutePath.TrimEnd('/') == "/api/face-inference")
            return new Uri(face.GetLeftPart(UriPartial.Authority) + "/");
        return null;
    }

    private string Setting(string name)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim();
        if (string.IsNullOrWhiteSpace(value)) _runtimeConfig.TryGetValue(name, out value);
        return name == "PICER_FFMPEG_PATH" ? MediaAgentSettings.ResolveFfmpeg(value, AppContext.BaseDirectory) : value ?? "";
    }

    private static async Task<ReadOnlyMemory<byte>?> ReceiveTextAsync(WebSocket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return message.ToArray();
        }
    }

    private async Task ServeAssetAsync(ClientWebSocket socket, JsonElement request, CancellationToken cancellationToken)
    {
        using var foreground = _maintenanceActivity.EnterForeground();
        var requestId = request.TryGetProperty("requestId", out var id) ? id.GetString() : null;
        var requestedPath = request.TryGetProperty("path", out var path) ? path.GetString() : null;
        if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(requestedPath)) return;
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var fullPath = Path.GetFullPath(requestedPath);
            if (!_roots.Any(root => fullPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) throw new UnauthorizedAccessException("asset path is outside configured roots");
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length > MaxAssetBytes) throw new FileNotFoundException("asset is missing or too large", fullPath);
            var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
            await SendAsync(socket, new { type = "asset.response", requestId, contentType = Mime(fullPath), contentBase64 = Convert.ToBase64String(bytes) }, cancellationToken);
            Log($"asset completed; requestId={requestId}; bytes={bytes.Length}; elapsedMs={started.ElapsedMilliseconds}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"asset failed; requestId={requestId}; errorType={exception.GetType().Name}; elapsedMs={started.ElapsedMilliseconds}");
            await SendAsync(socket, new { type = "asset.response", requestId, error = exception.Message }, cancellationToken);
        }
    }

    private async Task ServeAiAsync(ClientWebSocket socket, JsonElement request, CancellationToken cancellationToken)
    {
        using var foreground = _maintenanceActivity.EnterForeground();
        var requestId = request.TryGetProperty("requestId", out var id) ? id.GetString() : null;
        if (string.IsNullOrWhiteSpace(requestId) || !request.TryGetProperty("payloadBase64", out var payloadElement)) return;
        var started = System.Diagnostics.Stopwatch.StartNew();
        Log($"ai request accepted; requestId={requestId}; socketState={socket.State}");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_aiRequests.TryAdd(requestId, linked)) return;
        try
        {
            var uri = ModelEndpointResolver.Resolve(Setting("PICER_LOCAL_MODEL_ENDPOINT"));
            using var upstream = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new ByteArrayContent(Convert.FromBase64String(payloadElement.GetString() ?? ""))
            };
            upstream.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var response = await _modelClient.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            Log($"ai upstream response; requestId={requestId}; endpoint={uri.Host}{uri.AbsolutePath}; status={(int)response.StatusCode}; elapsedMs={started.ElapsedMilliseconds}");
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"model_http_{(int)response.StatusCode}");
            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
            var buffer = new byte[32 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, linked.Token)) > 0)
                await SendAsync(socket, new { type = "ai.chunk", requestId, contentBase64 = Convert.ToBase64String(buffer, 0, read) }, linked.Token);
            await SendAsync(socket, new { type = "ai.done", requestId }, cancellationToken);
            Log($"ai request completed; requestId={requestId}; elapsedMs={started.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Log($"ai request failed; requestId={requestId}; elapsedMs={started.ElapsedMilliseconds}; exception={exception.GetType().Name}; inner={(exception.InnerException?.GetType().Name ?? "none")}; message={exception.Message}");
            try { await SendAsync(socket, new { type = "ai.error", requestId, message = exception.Message }, cancellationToken); } catch { }
        }
        finally { _aiRequests.TryRemove(requestId, out _); }
    }

    private async Task ServeTaskAsync(ClientWebSocket socket, JsonElement request, CancellationToken cancellationToken)
    {
        var requestId = request.TryGetProperty("requestId", out var id) ? id.GetString() : null;
        var task = request.TryGetProperty("task", out var taskElement) ? taskElement.GetString() : null;
        var payload = request.TryGetProperty("payload", out var payloadElement) ? payloadElement.GetString() : null;
        var streamResult = request.TryGetProperty("stream", out var streamElement) && streamElement.ValueKind == JsonValueKind.True;
        if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(task)) return;
        if (payload is not null && payload.Length > MaxTaskPayloadChars)
        {
            await SendAsync(socket, new { type = "task.error", requestId, message = "task_payload_too_large" }, cancellationToken);
            return;
        }
        var started = System.Diagnostics.Stopwatch.StartNew();
        var maintenance = task == Picer.Core.Indexing.MediaConsistencyVerifier.Capability;
        using var foreground = maintenance ? null : _maintenanceActivity.EnterForeground();
        if (!maintenance) Log($"task accepted; requestId={requestId}; task={task}; payloadBytes={payload?.Length ?? 0}");
        try
        {
            if (maintenance)
            {
                var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                var check = JsonSerializer.Deserialize<Picer.Core.Indexing.MediaConsistencyRequest>(payload ?? "", options)
                    ?? throw new InvalidDataException("invalid_maintenance_request");
                using var background = _maintenanceActivity.TryEnterBackground(cancellationToken);
                var result = background is null ? new Picer.Core.Indexing.MediaConsistencyBatch([], Deferred: true)
                    : await new Picer.Core.Indexing.MediaConsistencyVerifier().VerifyAsync(check, _roots, background.Token);
                await SendAsync(socket, new { type = "task.result", requestId, payload = JsonSerializer.Serialize(result, options) }, cancellationToken);
                return;
            }
            if (!string.Equals(task, "index.repair", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(task, "directory.media", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(task, "video.purge-cache", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(task, "ffmpeg.probe", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(task, "ffmpeg.audio_extract", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(task, "ffmpeg.keyframes", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(task, "asr.transcribe", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(task, "asr.stream", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(task, "tts.synthesize", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(task, "vl.analyze", StringComparison.OrdinalIgnoreCase) &&
                !task.StartsWith("face.", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(task, "training.proxy", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(task, "video.people", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("task_not_supported");
            if (string.IsNullOrWhiteSpace(payload)) throw new InvalidOperationException("task_payload_required");
            if (string.Equals(task, "video.purge-cache", StringComparison.OrdinalIgnoreCase))
            {
                var purge = JsonSerializer.Deserialize<Picer.Core.Data.VideoRecordPurger.SourceCacheRequest>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidDataException("invalid_video_purge_request");
                var source = Path.GetFullPath(purge.Path);
                foreach (var candidate in purge.Keyframes.Prepend(source))
                    if (!_roots.Any(root => Picer.Core.Indexing.DirectoryMediaImport.Contains(root, Path.GetFullPath(candidate))))
                        throw new UnauthorizedAccessException("video_purge_outside_configured_roots");
                using var removal = await Picer.Core.Video.VideoOperationLifetime.BeginPurgeAsync(source, cancellationToken);
                _mediaLeases.Clear();
                _mediaPreviews.Clear();
                var cacheLeases = new List<IDisposable>();
                try
                {
                    foreach (var frame in purge.Keyframes.Distinct(StringComparer.OrdinalIgnoreCase))
                        cacheLeases.Add(await Picer.Core.Video.VideoOperationLifetime.BeginPurgeAsync(frame, cancellationToken));
                    var caches = purge.Keyframes.Select(Picer.Core.Data.VideoRecordPurger.RecordedSourceCacheDirectory).Where(path => path is not null).Cast<string>()
                        .Append(Picer.Core.Data.VideoRecordPurger.SourceCacheDirectory(source)).Distinct(StringComparer.OrdinalIgnoreCase);
                    foreach (var cache in caches) cacheLeases.Add(await Picer.Core.Video.VideoOperationLifetime.BeginPurgeAsync(cache, cancellationToken));
                    Picer.Core.Data.VideoRecordPurger.PurgeSourceCaches(purge with { Path = source });
                    Picer.Core.Indexing.DirectoryMediaChangeTracker.Shared.ForgetFile(source);
                    Picer.Core.Video.LocalVideoContentHashCache.ForgetFile(source);
                }
                finally { foreach (var lease in cacheLeases) lease.Dispose(); }
                await SendAsync(socket, new { type = "task.result", requestId, payload = "true" }, cancellationToken);
                return;
            }
            if (string.Equals(task, "index.repair", StringComparison.OrdinalIgnoreCase))
            {
                var operation = JsonSerializer.Deserialize<Picer.Core.Indexing.IndexRepairOperation>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidDataException("invalid_repair_request");
                foreach (var path in new[] { operation.OldPath, operation.NewPath }.Where(path => path is not null))
                {
                    var full = Path.GetFullPath(Picer.Core.Indexing.IndexRepairService.LocalPath(path!));
                    if (!_roots.Any(root => Picer.Core.Indexing.DirectoryMediaImport.Contains(root, full)))
                        throw new UnauthorizedAccessException("repair_outside_configured_roots");
                }
                var evidence = await Picer.Core.Indexing.IndexRepairService.VerifyAsync(operation, cancellationToken);
                await SendAsync(socket, new { type = "task.result", requestId, payload = JsonSerializer.Serialize(evidence, new JsonSerializerOptions(JsonSerializerDefaults.Web)) }, cancellationToken);
                return;
            }
            if (string.Equals(task, "directory.media", StringComparison.OrdinalIgnoreCase))
            {
                var scan = JsonSerializer.Deserialize<Picer.Core.Indexing.DirectoryMediaScanRequest>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidDataException("invalid_directory_request");
                var directory = Path.GetFullPath(scan.Directory).TrimEnd(Path.DirectorySeparatorChar);
                if (!_roots.Any(root => directory.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                    directory.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    throw new UnauthorizedAccessException("directory_outside_configured_roots");
                var page = await Task.Run(() => _directoryScanner.ReadAsync(scan, cancellationToken), cancellationToken);
                await SendAsync(socket, new { type = "task.result", requestId, payload = JsonSerializer.Serialize(page, new JsonSerializerOptions(JsonSerializerDefaults.Web)) }, cancellationToken);
                return;
            }
            if (string.Equals(task, "tts.synthesize", StringComparison.OrdinalIgnoreCase))
            {
                var endpointText = Setting("PICER_LOCAL_TTS_ENDPOINT");
                if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint)) throw new InvalidOperationException("tts_endpoint_not_configured");
                // The service contract deliberately exposes the portable
                // `{text}` field.  The bundled sherpa HTTP server calls that
                // field `input`; normalize at the agent boundary so callers
                // never need to know which local TTS implementation is used.
                using var ttsDocument = JsonDocument.Parse(payload);
                var ttsPayload = payload;
                if (ttsDocument.RootElement.TryGetProperty("text", out var textElement) &&
                    !ttsDocument.RootElement.TryGetProperty("input", out _))
                {
                    ttsPayload = JsonSerializer.Serialize(new
                    {
                        model = Setting("PICER_LOCAL_TTS_MODEL") ?? "sherpa-onnx-vits-zh-ll",
                        input = textElement.GetString(),
                        voice = 0,
                        speed = 1.0
                    });
                }
                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(ttsPayload, Encoding.UTF8, "application/json")
                };
                AddModelAuthorization(requestMessage);
                using var response = await _modelClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                var responseContentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                var responsePayload = responseContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                    ? JsonSerializer.Serialize(new { audioBase64 = Convert.ToBase64String(responseBytes), contentType = responseContentType })
                    : Encoding.UTF8.GetString(responseBytes);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"tts_http_{(int)response.StatusCode}:{Encoding.UTF8.GetString(responseBytes)}");
                if (streamResult)
                {
                    // The stream route is an audio byte stream, not the JSON
                    // envelope used by the non-stream route.
                    var bytes = responseContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                        ? responseBytes
                        : Encoding.UTF8.GetBytes(responsePayload);
                    foreach (var chunk in bytes.Chunk(32 * 1024)) await SendAsync(socket, new { type = "task.chunk", requestId, contentBase64 = Convert.ToBase64String(chunk) }, cancellationToken);
                }
                await SendAsync(socket, new { type = "task.result", requestId, payload = responsePayload }, cancellationToken);
                return;
            }
            if (string.Equals(task, "asr.stream", StringComparison.OrdinalIgnoreCase))
            {
                using var audioDocument = JsonDocument.Parse(payload);
                var endpointText = Setting("PICER_LOCAL_ASR_ENDPOINT");
                if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) || !audioDocument.RootElement.TryGetProperty("audioBase64", out var audioElement)) throw new InvalidOperationException("asr_stream_payload_invalid");
                var audioBytes = Convert.FromBase64String(audioElement.GetString() ?? "");
                if (audioBytes.Length > 32L * 1024 * 1024) throw new InvalidOperationException("audio_payload_too_large");
                using var form = new MultipartFormDataContent(); using var audio = new ByteArrayContent(audioBytes);
                audio.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav"); form.Add(audio, "file", "microphone.wav"); form.Add(new StringContent("qwen3-asr-1.7b"), "model"); form.Add(new StringContent("zh"), "language");
                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
                AddModelAuthorization(requestMessage);
                using var response = await _modelClient.SendAsync(requestMessage, cancellationToken); var text = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"asr_http_{(int)response.StatusCode}:{text}");
                await SendAsync(socket, new { type = "task.result", requestId, payload = text }, cancellationToken); return;
            }
            if (string.Equals(task, "vl.analyze", StringComparison.OrdinalIgnoreCase))
            {
                var endpointText = Setting("PICER_LOCAL_VL_ENDPOINT");
                if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint)) throw new InvalidOperationException("vl_endpoint_not_configured");
                var facePayload = await HydrateFaceImagePayloadAsync(payload, cancellationToken);
                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(facePayload, Encoding.UTF8, "application/json")
                };
                AddModelAuthorization(requestMessage);
                using var response = await _modelClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var responsePayload = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"vl_http_{(int)response.StatusCode}:{responsePayload}");
                if (streamResult)
                {
                    var bytes = Encoding.UTF8.GetBytes(responsePayload);
                    foreach (var chunk in bytes.Chunk(32 * 1024)) await SendAsync(socket, new { type = "task.chunk", requestId, contentBase64 = Convert.ToBase64String(chunk) }, cancellationToken);
                }
                await SendAsync(socket, new { type = "task.result", requestId, payload = responsePayload }, cancellationToken);
                return;
            }
            if (string.Equals(task, "video.people", StringComparison.OrdinalIgnoreCase))
            {
                using var videoDocument = JsonDocument.Parse(payload);
                if (!videoDocument.RootElement.TryGetProperty("keyframes", out var keyframesElement) || keyframesElement.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("video_people_keyframes_required");
                var endpointText = Setting("PICER_LOCAL_FACE_ENDPOINT");
                if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint)) throw new InvalidOperationException("face_endpoint_not_configured");
                var requestedModelVersion = videoDocument.RootElement.TryGetProperty("modelVersion", out var modelElement)
                    ? modelElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(requestedModelVersion) ||
                    !SupportedVideoFaceModelPriority.Contains(requestedModelVersion, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("video_people_model_not_supported");

                var candidates = new List<string> { requestedModelVersion };
                if (videoDocument.RootElement.TryGetProperty("modelPriority", out var priorityElement) && priorityElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in priorityElement.EnumerateArray())
                    {
                        var candidate = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(candidate) &&
                            SupportedVideoFaceModelPriority.Contains(candidate, StringComparer.OrdinalIgnoreCase) &&
                            !candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                            candidates.Add(candidate);
                    }
                }
                foreach (var fallback in SupportedVideoFaceModelPriority)
                    if (!candidates.Contains(fallback, StringComparer.OrdinalIgnoreCase)) candidates.Add(fallback);

                VideoFaceModelAttempt? completeEmpty = null;
                VideoFaceModelAttempt? firstFailure = null;
                VideoFaceModelAttempt? selected = null;
                foreach (var modelVersion in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attempt = await AnalyzeVideoFramesForModelAsync(videoDocument.RootElement, keyframesElement,
                        endpoint, modelVersion, cancellationToken);
                    if (attempt.Unsupported)
                    {
                        Log($"video.people model skipped; videoId={videoDocument.RootElement.GetProperty("videoId")}; model={modelVersion}; reason=unsupported");
                        continue;
                    }
                    if (attempt.Fatal)
                    {
                        firstFailure ??= attempt;
                        break;
                    }
                    if (attempt.FailedFrames == 0)
                    {
                        if (attempt.Faces.Count > 0)
                        {
                            selected = attempt;
                            break;
                        }
                        completeEmpty ??= attempt;
                    }
                    else firstFailure ??= attempt;
                }

                selected ??= completeEmpty ?? firstFailure;
                selected ??= new VideoFaceModelAttempt(requestedModelVersion, [], 0, 1, false);
                var fallbackUsed = !selected.ModelVersion.Equals(requestedModelVersion, StringComparison.OrdinalIgnoreCase);
                Log($"video.people completed; model={selected.ModelVersion}; fallbackUsed={fallbackUsed}; frames={selected.FrameCount}; failedFrames={selected.FailedFrames}; faces={selected.Faces.Count}");
                var result = new
                {
                    schemaVersion = "picer.face.v1",
                    operation = "video.people",
                    modelVersion = selected.ModelVersion,
                    faces = selected.Faces,
                    videoId = videoDocument.RootElement.TryGetProperty("videoId", out var videoIdElement) ? videoIdElement.ToString() : null,
                    error = selected.FailedFrames > 0 ? $"frames_failed:{selected.FailedFrames}" : null
                };
                await SendAsync(socket, new { type = "task.result", requestId, payload = JsonSerializer.Serialize(result) }, cancellationToken);
                return;
            }
            if (task == "training.proxy")
            {
                var envelope = JsonSerializer.Deserialize<Picer.Core.People.TrainingRelayRequest>(payload)
                    ?? throw new InvalidDataException("training_payload_invalid");
                if (!Picer.Core.People.TrainingRelayContract.IsAllowed(envelope.Method, envelope.Path))
                    throw new InvalidDataException("training_route_invalid");
                var endpoint = TrainingEndpoint() ?? throw new InvalidOperationException("training_endpoint_not_configured");
                using var message = new HttpRequestMessage(new HttpMethod(envelope.Method),
                    new Uri(endpoint, "api/face-training/" + envelope.Path));
                if (envelope.Body is not null) message.Content = new StringContent(envelope.Body, Encoding.UTF8, "application/json");
                AddModelAuthorization(message);
                using var response = await _modelClient.SendAsync(message, cancellationToken);
                var result = new Picer.Core.People.TrainingRelayResponse((int)response.StatusCode,
                    await response.Content.ReadAsStringAsync(cancellationToken));
                await SendAsync(socket, new { type = "task.result", requestId, payload = JsonSerializer.Serialize(result) }, cancellationToken);
                return;
            }
            if (task.StartsWith("face.", StringComparison.OrdinalIgnoreCase))
            {
                var endpointText = Setting("PICER_LOCAL_FACE_ENDPOINT");
                if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint)) throw new InvalidOperationException("face_endpoint_not_configured");
                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                AddModelAuthorization(requestMessage);
                requestMessage.Headers.Add("X-Picer-Face-Operation", task["face.".Length..]);
                using var response = await _modelClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var responsePayload = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"face_http_{(int)response.StatusCode}:{responsePayload}");
                await SendAsync(socket, new { type = "task.result", requestId, payload = responsePayload }, cancellationToken);
                return;
            }
            var fullPath = Path.GetFullPath(payload);
            using var mediaOperation = Picer.Core.Video.VideoOperationLifetime.Begin(fullPath, cancellationToken);
            cancellationToken = mediaOperation.Token;
            if (!_roots.Any(root => fullPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                throw new UnauthorizedAccessException("asset path is outside configured roots");
            var ffmpeg = Setting("PICER_FFMPEG_PATH");
            if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg)) throw new FileNotFoundException("ffmpeg is not configured");
            var probe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
            if (string.Equals(task, "ffmpeg.keyframes", StringComparison.OrdinalIgnoreCase))
            {
                var cacheRoot = Path.Combine(Path.GetDirectoryName(fullPath)!, ".picer-keyframes", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(fullPath))).ToLowerInvariant());
                using var cacheOperation = Picer.Core.Video.VideoOperationLifetime.Begin(cacheRoot, cancellationToken);
                cancellationToken = cacheOperation.Token;
                using var generation = new Picer.Core.Video.VideoKeyframeGeneration(cacheRoot);
                var cache = generation.Workspace;
                var pattern = Path.Combine(cache, "frame-%05d.jpg");
                var keyframeStart = new System.Diagnostics.ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-i", fullPath, "-vf", "select=gt(scene\\,0.35),scale=1280:-2,format=yuvj420p", "-fps_mode", "vfr", "-frames:v", "200", "-q:v", "4", "-y", pattern }) keyframeStart.ArgumentList.Add(argument);
                using var keyframeProcess = System.Diagnostics.Process.Start(keyframeStart) ?? throw new InvalidOperationException("ffmpeg_start_failed");
                using var keyframeProcessCancellation = cancellationToken.Register(() => StopMediaProcess(keyframeProcess));
                var keyframeError = await keyframeProcess.StandardError.ReadToEndAsync(cancellationToken);
                await keyframeProcess.WaitForExitAsync(cancellationToken);
                if (keyframeProcess.ExitCode != 0) throw new InvalidOperationException($"ffmpeg_keyframes_failed:{keyframeError.Trim()}");
                var frames = Directory.EnumerateFiles(cache, "frame-*.jpg").Order(StringComparer.OrdinalIgnoreCase).Take(200)
                    .Select((path, index) => new { timestampSeconds = index, path }).ToArray();
                // A clip without scene cuts is still a valid video.  Keep a
                // deterministic poster/keyframe instead of reporting an
                // empty result (the old behavior made ordinary videos look
                // like a failed service).
                if (frames.Length == 0)
                {
                    var fallback = Path.Combine(cache, "frame-00001.jpg");
                    var fallbackStart = new System.Diagnostics.ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-i", fullPath, "-frames:v", "1", "-q:v", "4", "-y", fallback }) fallbackStart.ArgumentList.Add(argument);
                    using var fallbackProcess = System.Diagnostics.Process.Start(fallbackStart) ?? throw new InvalidOperationException("ffmpeg_start_failed");
                    using var fallbackProcessCancellation = cancellationToken.Register(() => StopMediaProcess(fallbackProcess));
                    var fallbackError = await fallbackProcess.StandardError.ReadToEndAsync(cancellationToken);
                    await fallbackProcess.WaitForExitAsync(cancellationToken);
                    if (fallbackProcess.ExitCode != 0 || !File.Exists(fallback)) throw new InvalidOperationException($"ffmpeg_keyframes_empty:{fallbackError.Trim()}");
                    frames = [new { timestampSeconds = 0, path = fallback }];
                }
                await SendAsync(socket, new { type = "task.result", requestId, payload = JsonSerializer.Serialize(generation.Publish(frames.Select(frame => new Picer.Core.Video.VideoKeyframe(frame.timestampSeconds, frame.path)).ToArray())) }, cancellationToken);
                return;
            }
            if (string.Equals(task, "ffmpeg.audio_extract", StringComparison.OrdinalIgnoreCase) || string.Equals(task, "asr.transcribe", StringComparison.OrdinalIgnoreCase))
            {
                var temporary = Path.Combine(Path.GetTempPath(), $"picer-audio-{Guid.NewGuid():N}.wav");
                try
                {
                    var audioStart = new System.Diagnostics.ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-i", fullPath, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-y", temporary }) audioStart.ArgumentList.Add(argument);
                    using var audioProcess = System.Diagnostics.Process.Start(audioStart) ?? throw new InvalidOperationException("ffmpeg_start_failed");
                    using var audioProcessCancellation = cancellationToken.Register(() => StopMediaProcess(audioProcess));
                    var audioError = await audioProcess.StandardError.ReadToEndAsync(cancellationToken);
                    await audioProcess.WaitForExitAsync(cancellationToken);
                    if (audioProcess.ExitCode != 0) throw new InvalidOperationException($"ffmpeg_audio_failed:{audioError.Trim()}");
                    var info = new FileInfo(temporary);
                    if (!info.Exists || info.Length == 0 || info.Length > 32L * 1024 * 1024) throw new InvalidOperationException("audio_output_invalid_or_too_large");
                    if (string.Equals(task, "asr.transcribe", StringComparison.OrdinalIgnoreCase))
                    {
                        var endpointText = Setting("PICER_LOCAL_ASR_ENDPOINT");
                        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint)) throw new InvalidOperationException("asr_endpoint_not_configured");
                        using var form = new MultipartFormDataContent();
                        await using var stream = File.OpenRead(temporary);
                        using var file = new StreamContent(stream);
                        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
                        form.Add(file, "file", "audio-16k.wav");
                        form.Add(new StringContent(Environment.GetEnvironmentVariable("PICER_LOCAL_ASR_MODEL")?.Trim() ?? "qwen3-asr-1.7b"), "model");
                        form.Add(new StringContent("zh"), "language");
                        form.Add(new StringContent("loose"), "vad_mode");
                        form.Add(new StringContent("true"), "full_audio_fallback");
                        form.Add(new StringContent("true"), "timestamps");
                        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
                        AddModelAuthorization(requestMessage);
                        using var response = await _modelClient.SendAsync(requestMessage, cancellationToken);
                        var content = await response.Content.ReadAsStringAsync(cancellationToken);
                        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"asr_http_{(int)response.StatusCode}:{content}");
                        await SendAsync(socket, new { type = "task.result", requestId, payload = content }, cancellationToken);
                        return;
                    }
                    var encoded = Convert.ToBase64String(await File.ReadAllBytesAsync(temporary, cancellationToken));
                    await SendAsync(socket, new { type = "task.result", requestId, payload = JsonSerializer.Serialize(new { contentType = "audio/wav", contentBase64 = encoded }) }, cancellationToken);
                    return;
                }
                finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
            }
            if (!File.Exists(probe)) throw new FileNotFoundException("ffprobe is not available");
            var start = new System.Diagnostics.ProcessStartInfo(probe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height:format=duration", "-of", "json", fullPath }) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("ffprobe_start_failed");
            using var processCancellation = cancellationToken.Register(() => StopMediaProcess(process));
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0) throw new InvalidOperationException($"ffprobe_failed:{stderr.Trim()}");
            await SendAsync(socket, new { type = "task.result", requestId, payload = stdout }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log($"task canceled; requestId={requestId}; task={task}; elapsedMs={started.ElapsedMilliseconds}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log($"task failed; requestId={requestId}; task={task}; elapsedMs={started.ElapsedMilliseconds}; error={exception.GetType().Name}");
            try { await SendAsync(socket, new { type = "task.error", requestId, message = exception.Message }, cancellationToken); } catch { }
        }
        finally
        {
            if (_taskRequests.TryRemove(requestId, out var registration)) registration.Dispose();
        }
    }

    private async Task<VideoFaceModelAttempt> AnalyzeVideoFramesForModelAsync(JsonElement video,
        JsonElement keyframes, Uri endpoint, string modelVersion, CancellationToken cancellationToken)
    {
        var faces = new List<object>();
        var frameCount = 0;
        var failedFrames = 0;
        foreach (var frame in keyframes.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!frame.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var keyframeId) ||
                !frame.TryGetProperty("timestampSeconds", out var timeElement) || !timeElement.TryGetDouble(out var timestamp) ||
                !frame.TryGetProperty("path", out var pathElement) || string.IsNullOrWhiteSpace(pathElement.GetString()))
            {
                failedFrames++;
                return new VideoFaceModelAttempt(modelVersion, faces, frameCount, failedFrames, Unsupported: false, Fatal: true);
            }
            frameCount++;
            var framePath = Path.GetFullPath(pathElement.GetString()!);
            if (!_roots.Any(root => framePath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) || !File.Exists(framePath))
            {
                Log($"video.people frame unavailable; videoId={video.GetProperty("videoId")}; keyframeId={keyframeId}");
                failedFrames++;
                return new VideoFaceModelAttempt(modelVersion, faces, frameCount, failedFrames, Unsupported: false, Fatal: true);
            }
            try
            {
                var framePayload = JsonSerializer.Serialize(new
                {
                    imageId = 0L,
                    path = framePath,
                    modelVersion,
                    operation = "detect"
                });
                framePayload = await HydrateFaceImagePayloadAsync(framePayload, cancellationToken);
                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(framePayload, Encoding.UTF8, "application/json")
                };
                AddModelAuthorization(requestMessage);
                requestMessage.Headers.Add("X-Picer-Face-Operation", "detect");
                using var response = await _modelClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    // A model-specific 4xx means this endpoint does not expose
                    // that model space. It is a fallback signal, not a reason to
                    // relabel the returned vectors or abort the whole request.
                    if (response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotFound or
                        System.Net.HttpStatusCode.UnprocessableEntity)
                        return new VideoFaceModelAttempt(modelVersion, [], frameCount, 0, Unsupported: true);
                    failedFrames++;
                    Log($"video.people frame failed; keyframeId={keyframeId}; model={modelVersion}; status={(int)response.StatusCode}");
                    return new VideoFaceModelAttempt(modelVersion, faces, frameCount, failedFrames, Unsupported: false, Fatal: true);
                }
                var responsePayload = await response.Content.ReadAsStringAsync(cancellationToken);
                var frameResult = JsonSerializer.Deserialize<FaceAdapterResult>(responsePayload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (frameResult is null || !string.Equals(frameResult.SchemaVersion, "picer.face.v1", StringComparison.Ordinal) ||
                    !string.Equals(frameResult.Operation, "detect", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(frameResult.ModelVersion))
                {
                    failedFrames++;
                    Log($"video.people frame failed; keyframeId={keyframeId}; model={modelVersion}; reason=invalid_result_schema");
                    return new VideoFaceModelAttempt(modelVersion, faces, frameCount, failedFrames, Unsupported: false, Fatal: true);
                }
                if (!string.Equals(frameResult.ModelVersion, modelVersion, StringComparison.OrdinalIgnoreCase))
                {
                    Log($"video.people model skipped; requested={modelVersion}; actual={frameResult.ModelVersion}; reason=model_space_mismatch");
                    return new VideoFaceModelAttempt(modelVersion, [], frameCount, 0, Unsupported: true);
                }
                faces.AddRange(frameResult.Faces.Select(face => (object)new
                {
                    ordinal = face.Ordinal,
                    timestampSeconds = timestamp,
                    x = face.X,
                    y = face.Y,
                    width = face.Width,
                    height = face.Height,
                    confidence = face.Confidence,
                    embedding = face.Embedding,
                    keyframeId
                }));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidOperationException or JsonException)
            {
                failedFrames++;
                if (exception is HttpRequestException httpException)
                    Log($"video.people frame failed; keyframeId={keyframeId}; model={modelVersion}; exception=HttpRequestException; status={(int?)httpException.StatusCode}; transport={httpException.HttpRequestError}; inner={httpException.InnerException?.GetType().Name ?? "none"}");
                else
                    Log($"video.people frame failed; keyframeId={keyframeId}; model={modelVersion}; exception={exception.GetType().Name}");
                return new VideoFaceModelAttempt(modelVersion, faces, frameCount, failedFrames, Unsupported: false, Fatal: true);
            }
        }
        return new VideoFaceModelAttempt(modelVersion, faces, frameCount, failedFrames, Unsupported: false);
    }

    private static void StopMediaProcess(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        catch (InvalidOperationException) { }
    }

    private async Task SendAsync(ClientWebSocket socket, object value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
        await _sendLock.WaitAsync(cancellationToken);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken); }
        finally { _sendLock.Release(); }
    }

    private void AddModelAuthorization(HttpRequestMessage request)
    {
        ModelRequestAuthorization.Apply(request, Setting("PICER_LOCAL_MODEL_API_KEY"), Picer.Core.People.ModelServerApiKey.Load);

    }

    private async Task<string> HydrateFaceImagePayloadAsync(string payload, CancellationToken cancellationToken)
    {
        var document = JsonNode.Parse(payload)?.AsObject()
            ?? throw new InvalidOperationException("face_request_invalid");
        if (document["imageBase64"]?.GetValue<string>() is { Length: > 0 }) return document.ToJsonString();
        var path = document["path"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("face_image_required");
        var fullPath = Path.GetFullPath(path);
        if (!_roots.Any(root => fullPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("face image is outside configured roots");
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length == 0 || info.Length > 10L * 1024 * 1024)
            throw new InvalidDataException("face image is missing or exceeds the 10 MiB service limit");
        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        document.Remove("path");
        document["imageBase64"] = Convert.ToBase64String(bytes);
        return document.ToJsonString();
    }

    private static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp", ".mp4" => "video/mp4", ".mov" => "video/quicktime", ".avi" => "video/x-msvideo", _ => "application/octet-stream"
    };

    public void Dispose()
    {
        _disposed = true;
        _stop.Cancel();
        _modelClient.Dispose();
        _mediaLeases.Dispose();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _icon.Visible = false;
        _icon.Dispose();
        // Native media work can take a moment to observe cancellation. Do not
        // dispose its send semaphore/token source while its finally blocks run.
        void ReleaseConnectionResources() { _sendLock.Dispose(); _stop.Dispose(); }
        if (_loop is null || _loop.IsCompleted) ReleaseConnectionResources();
        else _ = _loop.ContinueWith(_ => ReleaseConnectionResources(), TaskScheduler.Default);
    }
}
