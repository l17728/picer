namespace Picer.Core.Gateway;

/// <summary>Background work yields at safe page/transaction boundaries; cancellation never rolls back a completed page.</summary>
public sealed class ForegroundWorkPriority
{
    private readonly object _gate = new();
    private int _readers;
    private TaskCompletionSource _idle = Completed();
    public IDisposable Enter()
    {
        lock (_gate) { if (_readers++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        return new Lease(this);
    }
    public Task WaitAsync(CancellationToken token)
    { lock (_gate) return _idle.Task.WaitAsync(token); }
    private void Leave() { lock (_gate) if (--_readers == 0) _idle.TrySetResult(); }
    private static TaskCompletionSource Completed() { var value = new TaskCompletionSource(); value.SetResult(); return value; }
    private sealed class Lease(ForegroundWorkPriority owner) : IDisposable
    {
        private ForegroundWorkPriority? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Leave();
    }
}
