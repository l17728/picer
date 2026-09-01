using System.Windows.Threading;

namespace Picer.App.Infrastructure;

internal static class UiWork
{
    public const int DefaultBatchSize = 40;
    private static readonly SemaphoreSlim DatabaseGate = new(1, 1);

    public static Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken = default) =>
        Task.Run(work, cancellationToken);

    public static Task RunAsync(Action work, CancellationToken cancellationToken = default) =>
        Task.Run(work, cancellationToken);

    public static async Task<T> RunDatabaseAsync<T>(Func<T> work, CancellationToken cancellationToken = default)
    {
        await DatabaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(work, cancellationToken).ConfigureAwait(false); }
        finally { DatabaseGate.Release(); }
    }

    public static async Task RunDatabaseAsync(Action work, CancellationToken cancellationToken = default)
    {
        await DatabaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await Task.Run(work, cancellationToken).ConfigureAwait(false); }
        finally { DatabaseGate.Release(); }
    }

    public static async Task AddInBatchesAsync<T>(IEnumerable<T> items, Action<T> add,
        CancellationToken cancellationToken = default, int batchSize = DefaultBatchSize)
    {
        foreach (var batch in items.Chunk(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in batch) add(item);
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
    }

    public static async void Forget(this Task task, Action<Exception>? onError = null)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception exception) { onError?.Invoke(exception); }
    }
}
