using System.Diagnostics;

namespace Picer.Core.Logging;

/// <summary>Observes waits without abandoning a database operation or changing its cancellation policy.</summary>
internal static class AnalysisDatabaseWork
{
    public static async Task RunAsync(string operation, Action work, CancellationToken token,
        Func<Action, CancellationToken, Task> execute)
    {
        var trace = AnalysisTrace.Current ?? "none";
        var timer = Stopwatch.StartNew();
        var executing = 0;
        var outcome = "failed";
        using var finished = new CancellationTokenSource();
        var monitor = MonitorAsync();
        try
        {
            await execute(() =>
            {
                Interlocked.Exchange(ref executing, 1);
                if (timer.ElapsedMilliseconds >= 1000)
                    Write($"Analysis database acquired; attemptId={trace}; operation={operation}; queueMs={timer.ElapsedMilliseconds}");
                work();
            }, token).ConfigureAwait(false);
            outcome = "completed";
        }
        catch (OperationCanceledException) { outcome = "cancelled"; throw; }
        finally
        {
            await finished.CancelAsync().ConfigureAwait(false);
            await monitor.ConfigureAwait(false);
            if (timer.ElapsedMilliseconds >= 1000 || outcome != "completed")
                Write($"Analysis database finished; attemptId={trace}; operation={operation}; outcome={outcome}; elapsedMs={timer.ElapsedMilliseconds}");
        }

        async Task MonitorAsync()
        {
            using var tick = new PeriodicTimer(TimeSpan.FromSeconds(5));
            try
            {
                while (await tick.WaitForNextTickAsync(finished.Token).ConfigureAwait(false))
                    Write($"Analysis database waiting; attemptId={trace}; operation={operation}; phase={(Volatile.Read(ref executing) == 0 ? "queue" : "execute")}; elapsedMs={timer.ElapsedMilliseconds}");
            }
            catch (OperationCanceledException) when (finished.IsCancellationRequested) { }
        }
    }

    private static void Write(string message)
    {
        // Diagnostics must never prevent result preservation or budget cleanup.
        try { AnalysisDiagnosticLog.Info(message); }
        catch (Exception) { }
    }
}
