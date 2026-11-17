using System.Threading.Channels;

namespace Picer.Core.Logging;

/// <summary>Bounded, best-effort diagnostics must not delay cancellation or SQLite work.</summary>
public static class AnalysisDiagnosticLog
{
    private static readonly Channel<(bool Warning, string Message)> Queue = Channel.CreateBounded<(bool, string)>(
        new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private static long _dropped;

    static AnalysisDiagnosticLog() => _ = Task.Run(DrainAsync);

    public static void Info(string message) => Enqueue(false, message);
    public static void Warn(string message) => Enqueue(true, message);

    private static void Enqueue(bool warning, string message)
    {
        if (!Queue.Writer.TryWrite((warning, message))) Interlocked.Increment(ref _dropped);
    }

    private static async Task DrainAsync()
    {
        await foreach (var item in Queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0) AppLogger.Warn($"Analysis diagnostics dropped; count={dropped}");
                if (item.Warning) AppLogger.Warn(item.Message); else AppLogger.Info(item.Message);
            }
            catch { /* Diagnostic output has no effect on analysis or cancellation. */ }
        }
    }
}
