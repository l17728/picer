using System.Diagnostics;

namespace Picer.Core.Diagnostics;

public sealed record EndpointBenchmarkResult(string Name, Uri Endpoint, int Samples,
    int Successes, long P50Ms, long P95Ms, string? Error);

public sealed class EndpointBenchmarkService(HttpClient? client = null) : IDisposable
{
    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    private readonly bool _ownsClient = client is null;

    public async Task<EndpointBenchmarkResult> RunAsync(string name, Uri endpoint, int samples = 3,
        CancellationToken cancellationToken = default)
    {
        samples = Math.Clamp(samples, 1, 20); var times = new List<long>(); string? error = null;
        for (var i = 0; i < samples; i++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var sw = Stopwatch.StartNew();
            try { using var response = await _client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); response.EnsureSuccessStatusCode(); times.Add(sw.ElapsedMilliseconds); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { error = ex.GetType().Name; }
        }
        times.Sort();
        return new(name, endpoint, samples, times.Count, Percentile(times, .50), Percentile(times, .95), error);
    }

    private static long Percentile(IReadOnlyList<long> values, double percentile) => values.Count == 0 ? 0 : values[Math.Min(values.Count - 1, (int)Math.Ceiling(values.Count * percentile) - 1)];
    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
