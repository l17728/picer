using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Search;
using Picer.Core.Video;

namespace Picer.Core.Gateway;

public sealed partial class HttpIndexGateway : IIndexGateway, IMediaAssetGateway, IRangeMediaGateway, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private string? _mirrorEpoch;
    private string? _readSnapshot;
    public async Task ReleaseReadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = Interlocked.Exchange(ref _readSnapshot, null);
        if (snapshot is null) return;
        using var response = await _client.DeleteAsync("api/sync/v2/read-snapshots/" + Uri.EscapeDataString(snapshot), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public byte[]? DiscoveredSpkiPin { get; private set; }

    public HttpIndexGateway(HttpClient authorizedClient, bool ownsClient = false)
    {
        if (authorizedClient.BaseAddress is null || authorizedClient.BaseAddress.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("An authenticated HTTPS client with a base address is required.", nameof(authorizedClient));
        _client = authorizedClient;
        _ownsClient = ownsClient;
    }

    public HttpIndexGateway(string baseUrl, string bearerToken, byte[]? expectedSpkiPin = null, HttpClient? client = null)
    {
        if (client is null)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                AllowAutoRedirect = false,
                MaxConnectionsPerServer = 12
            };
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                var pin = ComputeSpkiPin(certificate);
                if (expectedSpkiPin is null)
                {
                    DiscoveredSpkiPin = pin;
                    return true;
                }
                return CryptographicOperations.FixedTimeEquals(pin, expectedSpkiPin);
            };
            // Metadata includes face vectors and can be tens of megabytes for a
            // large library. The request is still cancellation-bound by the
            // caller, but must not be cut off by the old 30-second transport
            // timeout while the UI is bootstrapping a remote mirror.
            _client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
            _ownsClient = true;
        }
        else
        {
            _client = client;
            _ownsClient = false;
        }
        _client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    }

    public static byte[] ComputeSpkiPin(X509Certificate2 certificate) => certificate.PublicKey.ExportSubjectPublicKeyInfo();

    public async Task<GatewayChangePage> GetChangesAsync(GatewayChangeRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _client.PostAsJsonAsync("api/gateway/v1/changes", request, Json, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            throw new NotSupportedException("mirror_change_feed_unavailable");
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<GatewayChangePage>(Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("mirror_change_feed_empty");
        if (!page.ResetRequired) Volatile.Write(ref _mirrorEpoch, page.Epoch);
        Volatile.Write(ref _readSnapshot, page.Snapshot);
        return page;
    }

    public async Task<GatewayHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendWithRetryAsync(token => _client.GetAsync("health", token), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GatewayHealth>(Json, cancellationToken)
            ?? throw new InvalidOperationException("Empty health payload.");
    }

    public async Task<GatewayPage> ListImagesAsync(long? sourceId = null, bool includeMissing = false, int pageSize = 200, string? cursor = null, CancellationToken cancellationToken = default)
    {
        var query = new List<string> { $"pageSize={Math.Clamp(pageSize, 1, 500)}", $"includeMissing={includeMissing}" };
        if (sourceId is not null) query.Add($"sourceId={sourceId}");
        if (!string.IsNullOrEmpty(cursor)) query.Add($"cursor={Uri.EscapeDataString(cursor)}");
        using var response = await SendWithRetryAsync(token => _client.GetAsync($"api/gateway/v1/images?{string.Join("&", query)}", token), cancellationToken);
        return await ReadPageAsync(response, cancellationToken);
    }

    public async Task<ImageRecord?> GetImageAsync(long id, CancellationToken cancellationToken = default)
    {
        using var response = await GetWithRetryAsync($"api/gateway/v1/images/{id}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ImageRecord>(Json, cancellationToken);
    }

    public async Task<Stream?> OpenThumbnailAsync(long imageId, CancellationToken cancellationToken = default)
    {
        var response = await GetMediaWithRetryAsync($"api/gateway/v1/thumbs/{imageId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }
        return await OwnResponseAsync(response, cancellationToken);
    }

    public async Task<Stream?> OpenFaceThumbnailAsync(long faceId, string? agentId = null, CancellationToken cancellationToken = default)
    {
        var query = string.IsNullOrWhiteSpace(agentId) ? "" : $"?agentId={Uri.EscapeDataString(agentId)}";
        var response = await GetMediaWithRetryAsync($"api/gateway/v1/face-thumbs/{faceId}{query}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) { response.Dispose(); return null; }
        return await OwnResponseAsync(response, cancellationToken);
    }

    public async Task<Stream?> OpenVideoThumbnailAsync(long videoId, string? agentId = null, CancellationToken cancellationToken = default)
    {
        var query = string.IsNullOrWhiteSpace(agentId) ? "" : $"?agentId={Uri.EscapeDataString(agentId)}";
        var response = await GetMediaWithRetryAsync($"api/gateway/v1/video-thumbs/{videoId}{query}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) { response.Dispose(); return null; }
        return await OwnResponseAsync(response, cancellationToken);
    }

    public async Task<Stream?> OpenOriginalImageAsync(long imageId, string? agentId = null, CancellationToken cancellationToken = default)
    {
        var query = string.IsNullOrWhiteSpace(agentId) ? "" : $"?agentId={Uri.EscapeDataString(agentId)}";
        var response = await _client.GetAsync($"api/gateway/v1/assets/images/{imageId}{query}", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) { response.Dispose(); return null; }
        return await OwnResponseAsync(response, cancellationToken);
    }

    public async Task<Stream?> OpenOriginalVideoAsync(long videoId, string? agentId = null, CancellationToken cancellationToken = default)
    {
        var query = string.IsNullOrWhiteSpace(agentId) ? "" : $"?agentId={Uri.EscapeDataString(agentId)}";
        var response = await _client.GetAsync($"api/gateway/v1/assets/videos/{videoId}{query}", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) { response.Dispose(); return null; }
        return await OwnResponseAsync(response, cancellationToken);
    }

    public async Task AnalyzeVideoPeopleAsync(long videoId, CancellationToken cancellationToken = default)
    {
        // A mutating request may already be running when the connection drops.
        // Never submit it again without a server-side idempotency key.
        using var response = await _client.PostAsync($"api/service/v1/video-people/{videoId}", content: null, cancellationToken);
        if (response.IsSuccessStatusCode) return;
        var diagnostic = "video_analysis_failed";
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = document.RootElement;
            var error = root.TryGetProperty("error", out var errorValue) ? errorValue.GetString() : null;
            var detail = root.TryGetProperty("detail", out var detailValue) ? detailValue.GetString() : null;
            var safeError = SafeVideoPeopleCode(error) ?? "video_analysis_failed";
            var safeDetail = SafeVideoPeopleCode(detail);
            diagnostic = safeDetail is null ? safeError : $"{safeError}:{safeDetail}";
            if (safeError == "face_model_mismatch" &&
                root.TryGetProperty("expected", out var expectedValue) && expectedValue.ValueKind == JsonValueKind.String &&
                root.TryGetProperty("actual", out var actualValue) && actualValue.ValueKind == JsonValueKind.String)
                diagnostic += $" (expected={expectedValue.GetString()}, actual={actualValue.GetString()})";
        }
        catch (JsonException) { }
        throw new HttpRequestException($"Video people analysis failed: {diagnostic} (HTTP {(int)response.StatusCode}).", null, response.StatusCode);
    }

    private static string? SafeVideoPeopleCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var safeCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "video_people_task_failed", "video_face_analysis_incomplete", "video_face_keyframe_id_required",
            "video_face_keyframe_id_unknown", "video_people_keyframes_required", "face_model_mismatch",
            "face_result_schema_invalid", "media_agent_offline", "face_endpoint_not_configured",
            "face_service_unavailable", "face_request_failed", "video_people_requires_insightface_model",
            "task_payload_required", "task_not_supported", "task_payload_too_large", "media_agent_disconnected"
        };
        var candidate = value.Trim();
        if (safeCodes.Contains(candidate)) return candidate;
        if (candidate.StartsWith("frames_failed:", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(candidate.AsSpan("frames_failed:".Length), out var failedFrames) && failedFrames > 0)
            return $"frames_failed:{failedFrames}";
        var separator = candidate.IndexOf(':');
        return separator > 0 && safeCodes.Contains(candidate[..separator]) ? candidate[..separator] : null;
    }

    public async Task<GatewayVideoPage> ListVideosAsync(long? sourceId = null, int pageSize = 200, string? cursor = null, CancellationToken cancellationToken = default)
    {
        var query = new List<string> { $"pageSize={Math.Clamp(pageSize, 1, 500)}" };
        if (sourceId is not null) query.Add($"sourceId={sourceId}");
        if (!string.IsNullOrWhiteSpace(cursor)) query.Add($"cursor={Uri.EscapeDataString(cursor)}");
        using var response = await SendWithRetryAsync(token => _client.GetAsync($"api/gateway/v1/videos?{string.Join("&", query)}", token), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<GatewayVideoPage>(Json, cancellationToken)
            ?? throw new InvalidOperationException("Empty video page payload.");
    }

    public async Task<VideoRecord?> GetVideoAsync(long id, CancellationToken cancellationToken = default)
    {
        using var response = await GetWithRetryAsync($"api/gateway/v1/videos/{id}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<VideoRecord>(Json, cancellationToken);
    }

    public async Task<Stream?> OpenVideoKeyframeAsync(long keyframeId, CancellationToken cancellationToken = default)
    {
        var response = await GetMediaWithRetryAsync($"api/gateway/v1/video-keyframes/{keyframeId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) { response.Dispose(); return null; }
        return await OwnResponseAsync(response, cancellationToken);
    }

    public async Task<Stream?> OpenVideoKeyframeByOrdinalAsync(long videoId, int ordinal, CancellationToken cancellationToken = default)
    {
        var response = await GetMediaWithRetryAsync($"api/gateway/v1/video-keyframes/by-video/{videoId}/{Math.Max(0, ordinal)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) { response.Dispose(); return null; }
        return await OwnResponseAsync(response, cancellationToken);
    }

    public async Task<GatewayPage> SearchAsync(SearchFilters filters, int pageSize = 200, string? cursor = null, CancellationToken cancellationToken = default)
    {
        var payload = new { filters, pageSize = Math.Clamp(pageSize, 1, 500), cursor };
        using var response = await SendWithRetryAsync(token => _client.PostAsJsonAsync("api/gateway/v1/search", payload, Json, token), cancellationToken);
        return await ReadPageAsync(response, cancellationToken);
    }

    private static async Task<GatewayPage> ReadPageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<GatewayPage>(Json, cancellationToken)
            ?? throw new InvalidOperationException("Empty page payload.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        cancellationToken.ThrowIfCancellationRequested();
        // Never surface arbitrary response bodies: they may contain private index
        // data. A small allowlist of machine-readable diagnostics is safe and
        // makes contract conflicts actionable without leaking payload content.
        var diagnostic = "";
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            try
            {
                var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken).ConfigureAwait(false);
                if (body.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String)
                {
                    var error = errorElement.GetString();
                    if (error is "face_model_mismatch" or "video_people_keyframes_required" or "video_people_requires_insightface_model")
                    {
                        diagnostic = $" ({error}";
                        if (error == "face_model_mismatch" &&
                            body.TryGetProperty("expected", out var expectedElement) && expectedElement.ValueKind == JsonValueKind.String &&
                            body.TryGetProperty("actual", out var actualElement) && actualElement.ValueKind == JsonValueKind.String)
                            diagnostic += $": expected={expectedElement.GetString()}, actual={actualElement.GetString()}";
                        diagnostic += ")";
                    }
                }
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException or IOException)
            {
                // Keep the status-only fallback for malformed or unexpected bodies.
            }
        }
        throw new HttpRequestException($"Gateway returned {(int)response.StatusCode} {response.StatusCode}{diagnostic}", null, response.StatusCode);
    }

    private Task<HttpResponseMessage> GetWithRetryAsync(string route, CancellationToken cancellationToken,
        [System.Runtime.CompilerServices.CallerMemberName] string operation = "")
        => SendWithRetryAsync(token => _client.GetAsync(route, token), cancellationToken, operation);

    private static async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken, [System.Runtime.CompilerServices.CallerMemberName] string operation = "")
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var delay = TimeSpan.FromMilliseconds(200 * (attempt + 1) + Random.Shared.Next(100));
            try
            {
                var response = await send(cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
                    HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                    || attempt == 2) return response;
                Logging.AppLogger.Warn($"Index gateway retry: operation={operation}; attempt={attempt + 1}/3; status={(int)response.StatusCode}");
                var retryAfter = response.Headers.RetryAfter?.Delta ?? response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow;
                if (retryAfter is { } requested && requested > delay)
                    delay = requested;
                // Do not retry before a long server-directed cooldown or keep a GUI
                // task asleep indefinitely. Return the busy response to the caller.
                if (delay > TimeSpan.FromSeconds(30)) return response;
                response.Dispose();
            }
            catch (HttpRequestException exception) when (attempt < 2)
            {
                last = exception;
                Logging.AppLogger.Warn($"Index gateway retry: operation={operation}; attempt={attempt + 1}/3; errorType={exception.GetType().Name}; innerType={exception.InnerException?.GetType().Name}; transportError={exception.HttpRequestError}");
            }
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        throw new HttpRequestException("远程索引服务重试失败", last);
    }

    public async Task<IReadOnlyList<SourceFolder>> ListSourcesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await GetWithRetryAsync("api/gateway/v1/sources", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<SourceFolder>>(Json, cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<Tag>> ListTagsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await GetSnapshotReadAsync("api/gateway/v1/tags", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<Tag>>(Json, cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<Album>> ListAlbumsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await GetSnapshotReadAsync("api/gateway/v1/albums", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<Album>>(Json, cancellationToken) ?? [];
    }

    public async Task<GatewayMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
        => await GetMetadataAsync(cancellationToken, includeEmbeddings: false, includeFaceModels: false);

    public async Task<GatewayMetadata> GetMetadataAsync(CancellationToken cancellationToken, bool includeEmbeddings)
        => await GetMetadataAsync(cancellationToken, includeEmbeddings, includeFaceModels: false);

    public async Task<GatewayMetadata> GetMetadataAsync(CancellationToken cancellationToken, bool includeEmbeddings, bool includeFaceModels)
    {
        using var response = await GetSnapshotReadAsync($"api/gateway/v1/metadata?includeEmbeddings={includeEmbeddings.ToString().ToLowerInvariant()}&includeFaceModels={includeFaceModels.ToString().ToLowerInvariant()}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<GatewayMetadata>(Json, cancellationToken)
            ?? new GatewayMetadata([], [], []);
    }

    private Task<HttpResponseMessage> GetSnapshotReadAsync(string path, CancellationToken token) => SendWithRetryAsync(async ct =>
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (Volatile.Read(ref _readSnapshot) is { } snapshot) request.Headers.Add("X-Picer-Read-Snapshot", snapshot);
        return await _client.SendAsync(request, ct).ConfigureAwait(false);
    }, token);

    public async Task<IReadOnlyList<PersonSummary>> ListPeopleAsync(CancellationToken cancellationToken = default)
    {
        using var response = await GetSnapshotReadAsync("api/gateway/v1/people", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<PersonSummary>>(Json, cancellationToken) ?? [];
    }

    public async Task<SourceFolder> RegisterSourceAsync(string path, CancellationToken cancellationToken = default)
    {
        using var response = await _client.PostAsJsonAsync("api/gateway/v1/sources", new { path }, Json, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SourceFolder>(Json, cancellationToken)
            ?? throw new InvalidOperationException("Empty source payload.");
    }

    public async Task<bool> RemoveSourceAsync(long sourceId, CancellationToken cancellationToken = default)
    {
        using var response = await _client.DeleteAsync($"api/gateway/v1/sources/{sourceId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        await EnsureSuccessAsync(response, cancellationToken);
        return true;
    }

    public async Task<string> StartScanAsync(long sourceId, CancellationToken cancellationToken = default)
    {
        using var response = await _client.PostAsJsonAsync("api/gateway/v1/scan", new { sourceId }, Json, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var started = await response.Content.ReadFromJsonAsync<GatewayScanStarted>(Json, cancellationToken)
            ?? throw new InvalidOperationException("Empty scan payload.");
        return started.JobId;
    }

    public async Task<ScanJobInfo?> GetScanJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync($"api/gateway/v1/jobs/{Uri.EscapeDataString(jobId)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<ScanJobInfo>(Json, cancellationToken);
    }

    public async Task<bool> CancelScanAsync(string jobId, CancellationToken cancellationToken = default)
    {
        using var response = await _client.PostAsJsonAsync($"api/gateway/v1/jobs/{Uri.EscapeDataString(jobId)}/cancel", new { }, Json, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        await EnsureSuccessAsync(response, cancellationToken);
        return true;
    }

    public async Task<int> RemoveRecycledRecordsAsync(IReadOnlyList<long> imageIds, CancellationToken cancellationToken = default)
    {
        using var response = await _client.PostAsJsonAsync("api/gateway/v1/verified-changes", new { imageIds }, Json, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<GatewayVerifiedChangesResult>(Json, cancellationToken)
            ?? throw new InvalidOperationException("Empty verified-changes payload.");
        return result.Removed;
    }

    public async Task<bool> PurgeVideoAsync(long id, CancellationToken cancellationToken = default)
    {
        using var response = await PostPurgeAsync($"api/gateway/v1/videos/{id}/purge", cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PipeFlagResponse>(Json, cancellationToken))?.Value == true;
    }

    public async Task<bool> PurgeMissingImageAsync(long id, CancellationToken cancellationToken = default)
    {
        using var response = await PostPurgeAsync($"api/gateway/v1/images/{id}/purge-missing", cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PipeFlagResponse>(Json, cancellationToken))?.Value == true;
    }

    private Task<HttpResponseMessage> PostPurgeAsync(string path, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new { }) };
        if (Volatile.Read(ref _mirrorEpoch) is { } epoch) request.Headers.Add("X-Picer-Mirror-Epoch", epoch);
        return SendPurgeAsync(request, cancellationToken);
    }
    private async Task<HttpResponseMessage> SendPurgeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { using (request) return await _client.SendAsync(request, cancellationToken).ConfigureAwait(false); }

    public async Task<string> BackupAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        using var response = await _client.PostAsJsonAsync("api/gateway/v1/backup", new { destinationPath }, Json, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<GatewayBackupResult>(Json, cancellationToken)
            ?? throw new InvalidOperationException("Empty backup payload.");
        return result.Path;
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}
