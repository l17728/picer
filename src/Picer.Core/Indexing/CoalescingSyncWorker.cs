using System.Threading.Channels;
using Picer.Core.Logging;

namespace Picer.Core.Indexing;

/// <summary>One worker and one pending wakeup regardless of producer count.</summary>
public sealed class CoalescingSyncWorker : IDisposable
{
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private int _disposed;
    public CoalescingSyncWorker(Func<CancellationToken, Task> synchronize, TimeSpan? retryDelay = null)
    {
        _worker = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in _signals.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        try { await synchronize(_stop.Token).ConfigureAwait(false); break; }
                        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                        catch (Exception exception)
                        {
                            AnalysisDiagnosticLog.Warn($"Description sync deferred; type={exception.GetType().Name}");
                            if (exception is UnauthorizedAccessException or ArgumentException or InvalidDataException ||
                                exception is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized } ||
                                exception is Microsoft.Data.Sqlite.SqliteException sqlite && sqlite.SqliteErrorCode is not (5 or 6)) break;
                            await Task.Delay(retryDelay ?? TimeSpan.FromSeconds(30 + Random.Shared.Next(6)), _stop.Token).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        });
    }
    public void Request() => _signals.Writer.TryWrite(true);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _signals.Writer.TryComplete(); _stop.Cancel();
        _worker.GetAwaiter().GetResult(); _stop.Dispose();
    }
}
