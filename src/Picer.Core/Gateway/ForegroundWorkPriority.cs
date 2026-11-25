namespace Picer.Core.Gateway;

/// <summary>Background work yields at safe page/transaction boundaries; cancellation never rolls back a completed page.</summary>
public sealed class ForegroundWorkPriority
{
    private readonly object _gate = new();
    private int _readers;
    private int _requiredWork;
    private TaskCompletionSource _required = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Explicitly requested synchronization must not starve behind a continuous thumbnail stream.</summary>
    public IDisposable RequireBackgroundProgress()
    {
        lock (_gate) { if (_requiredWork++ == 0) _required.TrySetResult(); }
        return new RequiredLease(this);
    }

    private void LeaveRequired()
    {
        lock (_gate)
            if (--_requiredWork == 0) _required = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RequiredLease(ForegroundWorkPriority owner) : IDisposable
    {
        private ForegroundWorkPriority? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.LeaveRequired();
    }

    private TaskCompletionSource _idle = Completed();
    public IDisposable Enter()
    {
        lock (_gate) { if (_readers++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        return new Lease(this);
    }
    public Task WaitAsync(CancellationToken token)
    { lock (_gate) return Task.WhenAny(_idle.Task, _required.Task).WaitAsync(token); }
    private void Leave() { lock (_gate) if (--_readers == 0) _idle.TrySetResult(); }
    private static TaskCompletionSource Completed() { var value = new TaskCompletionSource(); value.SetResult(); return value; }
    private sealed class Lease(ForegroundWorkPriority owner) : IDisposable
    {
        private ForegroundWorkPriority? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Leave();
    }
}
