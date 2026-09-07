using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Picer.Core.Vision;

public sealed record FaceEvaluationArtifactEnvelope(string ArtifactId, string Sha256,
    FaceAlgorithmBenchmarkResult Benchmark);

/// <summary>HTTPS-only transport for versioned evaluation artifacts; it never sends images or embeddings.</summary>
public sealed class FaceEvaluationRemoteClient(HttpClient? client = null) : IDisposable
{
    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    private readonly bool _ownsClient = client is null;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<FaceEvaluationArtifactEnvelope> PublishAsync(Uri endpoint,
        FaceAlgorithmBenchmarkResult benchmark, string? bearerToken = null,
        CancellationToken cancellationToken = default)
    {
        ValidateEndpoint(endpoint);
        var json = benchmark.ToJson();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        var artifactId = "face-eval-" + hash[..24];
        var envelope = new { artifactId, sha256 = hash, benchmark };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "api/face-evaluation/v1/artifacts"));
        request.Content = new StringContent(JsonSerializer.Serialize(envelope, Json), Encoding.UTF8, "application/json");
        if (!string.IsNullOrWhiteSpace(bearerToken)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadAndVerifyAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FaceEvaluationArtifactEnvelope> GetAsync(Uri endpoint, string artifactId,
        string? bearerToken = null, CancellationToken cancellationToken = default)
    {
        ValidateEndpoint(endpoint);
        if (string.IsNullOrWhiteSpace(artifactId) || artifactId.Any(char.IsWhiteSpace)) throw new ArgumentException("制品 ID 无效。", nameof(artifactId));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, $"api/face-evaluation/v1/artifacts/{Uri.EscapeDataString(artifactId)}"));
        if (!string.IsNullOrWhiteSpace(bearerToken)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadAndVerifyAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FaceEvaluationArtifactEnvelope> ReadAndVerifyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var envelope = await response.Content.ReadFromJsonAsync<FaceEvaluationArtifactEnvelope>(Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("远程评测制品响应为空。");
        var canonical = envelope.Benchmark.ToJson();
        var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        if (!string.Equals(actual, envelope.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("远程评测制品哈希校验失败。");
        return envelope;
    }

    private static void ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || (endpoint.Scheme != Uri.UriSchemeHttps && !endpoint.IsLoopback))
            throw new ArgumentException("远程评测服务必须使用 HTTPS；仅允许本机回环地址使用 HTTP。", nameof(endpoint));
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
