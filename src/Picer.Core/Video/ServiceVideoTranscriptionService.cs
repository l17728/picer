using System.Net.Http.Headers;

namespace Picer.Core.Video;

public sealed class ServiceVideoTranscriptionService(Uri serviceEndpoint, string bearerToken, HttpClient? client = null, bool disposeClient = false) : IVideoTranscriptionService, IDisposable
{
    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    private readonly bool _ownsClient = client is null || disposeClient;
    public string ModelVersion => "service-asr";

    public async Task<string> TranscribeAsync(VideoRecord video, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(serviceEndpoint, $"api/service/v1/video-transcribe/{video.Id}");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        if (!string.IsNullOrWhiteSpace(bearerToken)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Service ASR 请求失败（{(int)response.StatusCode}）：{content}");
        return string.IsNullOrWhiteSpace(content) ? throw new InvalidDataException("Service ASR 返回空结果") : content;
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
