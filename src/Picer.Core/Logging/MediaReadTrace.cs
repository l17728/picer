using System.Diagnostics;

namespace Picer.Core.Logging;

/// <summary>Safe stage timings only; never include paths, URLs, names or payloads.</summary>
public sealed class MediaReadTrace : IDisposable
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly IDisposable _scope;
    private readonly string _component;
    private readonly long _mediaId;
    private long _previous;
    public string Id { get; }
    public MediaReadTrace(string component, long mediaId = 0, string? traceId = null)
    {
        Id = Guid.TryParseExact(traceId, "N", out _) ? traceId! : AnalysisTrace.Current ?? Guid.NewGuid().ToString("N");
        _component = component; _mediaId = mediaId; _scope = AnalysisTrace.Begin(Id);
    }
    public void Mark(string stage, long bytes = 0, int status = 0)
    {
        var elapsed = _watch.ElapsedMilliseconds;
        var previous = Interlocked.Exchange(ref _previous, elapsed);
        AnalysisDiagnosticLog.Info($"MediaRead trace={Id}; component={_component}; mediaId={_mediaId}; stage={stage}; elapsedMs={elapsed}; stageMs={elapsed - previous}; bytes={bytes}; status={status}");
    }
    public void Failure(Exception exception) => AnalysisDiagnosticLog.Warn(
        $"MediaRead trace={Id}; component={_component}; mediaId={_mediaId}; stage=failed; elapsedMs={_watch.ElapsedMilliseconds}; type={exception.GetType().Name}; hresult={exception.HResult}; status={(exception is HttpRequestException http ? (int?)http.StatusCode : null)}");
    public void Dispose() => _scope.Dispose();
}
