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

public sealed class HttpIndexGateway : IIndexGateway, IMediaAssetGateway, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public byte[]? DiscoveredSpkiPin { get; private set; }

    public HttpIndexGateway(string baseUrl, string bearerToken, byte[]? expectedSpkiPin = null, HttpClient? client = null)
    {
        if (client is null)
        {
            var handler = new HttpClientHandler();
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
            _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
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
        using var response = await _client.GetAsync($"api/gateway/v1/images/{id}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ImageRecord>(Json, cancellationToken);
    }

    public async Task<Stream?> OpenThumbnailAsync(long imageId, CancellationToken cancellationToken = default)
    {
        var response = await _client.GetAsync($"api/gateway/v1/thumbs/{imageId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }

    public async Task<Stream?> OpenOriginalImageAsync(long imageId, string? agentId = null, CancellationToken cancellationToken = default)
    {
        var query = string.IsNullOrWhiteSpace(agentId) ? "" : $"?agentId={Uri.EscapeDataString(agentId)}";
        var response = await _client.GetAsync($"api/gateway/v1/assets/images/{imageId}{query}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) { response.Dispose(); return null; }
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadAsStreamAsync(cancellationToken);
    }

    public async Task<Stream?> OpenOriginalVideoAsync(long videoId, string? agentId = null, CancellationToken cancellationToken = default)
    {
        var query = string.IsNullOrWhiteSpace(agentId) ? "" : $"?agentId={Uri.EscapeDataString(agentId)}";
        var response = await _client.GetAsync($"api/gateway/v1/assets/videos/{videoId}{query}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) { response.Dispose(); return null; }
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadAsStreamAsync(cancellationToken);
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
        using var response = await _client.GetAsync($"api/gateway/v1/videos/{id}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<VideoRecord>(Json, cancellationToken);
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
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"Gateway returned {(int)response.StatusCode} {response.StatusCode}: {body}");
    }

    private static async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await send(cancellationToken).ConfigureAwait(false);
                if ((int)response.StatusCode < 500 || attempt == 2) return response;
                response.Dispose();
            }
            catch (HttpRequestException exception) when (attempt < 2)
            {
                last = exception;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
        }
        throw new HttpRequestException("远程索引服务重试失败", last);
    }

    public async Task<IReadOnlyList<SourceFolder>> ListSourcesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync("api/gateway/v1/sources", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<SourceFolder>>(Json, cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<Tag>> ListTagsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync("api/gateway/v1/tags", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<Tag>>(Json, cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<Album>> ListAlbumsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync("api/gateway/v1/albums", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<Album>>(Json, cancellationToken) ?? [];
    }

    public async Task<GatewayMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync("api/gateway/v1/metadata", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<GatewayMetadata>(Json, cancellationToken)
            ?? new GatewayMetadata([], [], []);
    }

    public async Task<IReadOnlyList<PersonSummary>> ListPeopleAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync("api/gateway/v1/people", cancellationToken);
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
