using Picer.Core.Indexing;
using Picer.Core.Logging;

namespace Picer.IndexService;

internal sealed class BackgroundConsistencyScheduler(MediaConsistencyMaintenance maintenance) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    public void Start() => _worker ??= Task.Run(RunAsync);
    private async Task RunAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), _stop.Token);
            while (!_stop.IsCancellationRequested)
            {
                try { await maintenance.RunOnceAsync(_stop.Token); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception exception) { AppLogger.Warn($"后台一致性检查暂缓：{exception.GetType().Name}"); }
                await Task.Delay(TimeSpan.FromSeconds(10), _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public void Stop() => _stop.Cancel();
    public void Dispose()
    {
        Stop();
        _worker?.GetAwaiter().GetResult();
        _stop.Dispose();
    }
}
