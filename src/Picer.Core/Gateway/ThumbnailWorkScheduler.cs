namespace Picer.Core.Gateway;

/// <summary>Viewport work overtakes queued prefetch; prefetch cannot occupy every worker.</summary>
public sealed class ThumbnailWorkScheduler
{
    private readonly object _sync = new();
    private readonly LinkedList<Waiter> _pending = new();
    private readonly int _maximum, _prefetchMaximum;
    private int _active, _prefetchActive;
    private sealed class Waiter(Func<bool> visible)
    {
        public readonly Func<bool> Visible = visible;
        public readonly TaskCompletionSource<IDisposable> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node;
    }

    public ThumbnailWorkScheduler(int maximum = 8, int maximumPrefetch = 1)
    {
        if (maximum < 1 || maximumPrefetch < 1 || maximumPrefetch > maximum) throw new ArgumentOutOfRangeException(nameof(maximum));
        _maximum = maximum; _prefetchMaximum = maximumPrefetch;
    }

    public async Task<IDisposable> EnterAsync(Func<bool> visible, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var waiter = new Waiter(visible);
        lock (_sync)
        {
            if (_pending.Count >= 256) throw new OperationCanceledException("thumbnail_queue_full", token);
            waiter.Node = _pending.AddLast(waiter);
            Dispatch();
        }
        using var registration = token.Register(() =>
        {
            lock (_sync)
            {
                if (waiter.Node is null) return;
                _pending.Remove(waiter.Node); waiter.Node = null;
                waiter.Ready.TrySetCanceled(token); Dispatch();
            }
        });
        var lease = await waiter.Ready.Task.ConfigureAwait(false);
        if (token.IsCancellationRequested) { lease.Dispose(); token.ThrowIfCancellationRequested(); }
        return lease;
    }

    public void RefreshPriorities() { lock (_sync) Dispatch(); }

    private void Dispatch()
    {
        while (_active < _maximum && _pending.Count > 0)
        {
            var waiter = _pending.FirstOrDefault(item => item.Visible());
            var prefetch = waiter is null;
            if (prefetch && _prefetchActive >= _prefetchMaximum) return;
            waiter ??= _pending.First!.Value;
            _pending.Remove(waiter.Node!); waiter.Node = null;
            _active++; if (prefetch) _prefetchActive++;
            waiter.Ready.TrySetResult(new Lease(this, prefetch));
        }
    }

    private sealed class Lease(ThumbnailWorkScheduler owner, bool prefetch) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._sync)
            {
                owner._active--; if (prefetch) owner._prefetchActive--;
                owner.Dispatch();
            }
        }
    }
}
