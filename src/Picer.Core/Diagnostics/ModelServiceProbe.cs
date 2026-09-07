using System.Diagnostics;
using System.Net.Http;

namespace Picer.Core.Diagnostics;

public sealed record ModelServiceProbeResult(string Name, Uri Endpoint, bool Reachable,
    bool Secure, long LatencyMs, string Detail);

public sealed class ModelServiceProbe(HttpClient? client = null) : IDisposable
{
    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    private readonly bool _ownsClient = client is null;

    public async Task<ModelServiceProbeResult> CheckAsync(string name, Uri endpoint,
        CancellationToken cancellationToken = default)
    {
        var secure = endpoint.Scheme == Uri.UriSchemeHttps || endpoint.IsLoopback;
        if (!secure) return new(name, endpoint, false, false, 0, "远程服务必须使用 HTTPS");
        var timer = Stopwatch.StartNew();
        try
        {
            using var response = await _client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return new(name, endpoint, true, endpoint.Scheme == Uri.UriSchemeHttps, timer.ElapsedMilliseconds,
                $"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(name, endpoint, false, endpoint.Scheme == Uri.UriSchemeHttps, timer.ElapsedMilliseconds, "连接超时"); }
        catch (Exception ex) { return new(name, endpoint, false, endpoint.Scheme == Uri.UriSchemeHttps, timer.ElapsedMilliseconds, ex.GetType().Name); }
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
