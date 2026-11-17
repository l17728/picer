using Picer.Core.Logging;

namespace Picer.App.Infrastructure;

/// <summary>Owns progress admission and unconditional cleanup for one directory run.</summary>
internal sealed class DescriptionRunLifetime
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public bool Cancelling { get; private set; }
    public bool Terminal { get; private set; }
    private bool _progressClosed;
    private string _latestProgress = "";
    public string LatestProgress => Volatile.Read(ref _latestProgress);
    public IProgress<string> CaptureProgress(IProgress<string> downstream) => new CapturedProgress(this, downstream);
    public bool AcceptsProgress => !Cancelling && !Terminal && !_progressClosed;
    public void CloseProgress() => _progressClosed = true;

    public bool RequestCancellation()
    {
        if (Cancelling || Terminal) return false;
        Cancelling = true;
        return true;
    }

    public async Task ObserveAsync(Func<Task> work, string phase)
    {
        try { await work(); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { LogFailure(phase, exception); }
    }

    public void Cleanup(Action work, string phase)
    {
        try { work(); }
        catch (Exception exception) { LogFailure(phase, exception); }
    }

    public async Task FinishAsync(Func<Task> drain, Action release, Action reset)
    {
        Terminal = true;
        try { await ObserveAsync(drain, "status_drain"); }
        finally
        {
            try { Cleanup(release, "resources"); }
            finally { reset(); }
        }
    }

    private void LogFailure(string phase, Exception exception)
    {
        try { AnalysisDiagnosticLog.Warn($"Directory description cleanup deferred; runId={Id}; phase={phase}; errorType={exception.GetType().Name}; hresult=0x{exception.HResult:X8}"); }
        catch { /* Diagnostics must never prevent subsequent resource cleanup. */ }
    }

    private sealed class CapturedProgress(DescriptionRunLifetime owner, IProgress<string> downstream) : IProgress<string>
    {
        public void Report(string value)
        {
            Volatile.Write(ref owner._latestProgress, value);
            downstream.Report(value);
        }
    }
}
