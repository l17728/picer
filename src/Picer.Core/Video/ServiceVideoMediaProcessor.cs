using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Picer.Core.Video;

/// <summary>FFmpeg proxy for Local/Remote GUI; execution remains on MediaAgent.</summary>
public sealed class ServiceVideoMediaProcessor : IVideoMediaProcessor, IDisposable
{
    private readonly HttpClient _client;
    private readonly string _baseUrl;
    private readonly string _cacheRoot;

    public ServiceVideoMediaProcessor(Uri serviceEndpoint, string bearerToken, string cacheRoot, HttpClient? client = null)
    {
        _baseUrl = serviceEndpoint.ToString().TrimEnd('/') + "/";
        _cacheRoot = cacheRoot;
        _client = client ?? new HttpClient();
        _client.BaseAddress = new Uri(_baseUrl);
        _client.Timeout = TimeSpan.FromMinutes(30);
        if (!string.IsNullOrWhiteSpace(bearerToken))
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    }

    public async Task<VideoProbeResult> ProbeAsync(VideoRecord video, CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetAsync($"api/service/v1/video-probe/{video.Id}", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<VideoProbeResult>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("IndexService returned an empty video probe");
    }

    public async Task<IReadOnlyList<VideoKeyframe>> ExtractAsync(VideoRecord video, CancellationToken cancellationToken = default)
    {
        using var response = await _client.PostAsync($"api/service/v1/video-keyframes/{video.Id}", content: null, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<List<VideoKeyframe>>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return result ?? [];
    }

    public async Task<string> GetOrCreatePosterAsync(VideoRecord video, CancellationToken cancellationToken = default)
    {
        using var operation = VideoOperationLifetime.Begin(video.Path, cancellationToken);
        cancellationToken = operation.Token;
        var directory = await VideoMediaCachePaths.DirectoryAsync(_cacheRoot, video, false, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(directory);
        var poster = Path.Combine(directory, "poster.jpg");
        if (File.Exists(poster)) return poster;
        var frames = await ExtractAsync(video, cancellationToken).ConfigureAwait(false);
        var first = frames.FirstOrDefault(frame => File.Exists(frame.Path));
        if (first is null) throw new InvalidOperationException("IndexService returned no keyframe poster");
        await using var input = File.OpenRead(first.Path);
        await VideoKeyframeCache.WriteAsync(input, poster, cancellationToken).ConfigureAwait(false);
        return poster;
    }

    public void Dispose() => _client.Dispose();
}
