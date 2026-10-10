namespace Picer.Core.Indexing;

/// <summary>Background I/O yields to foreground operations without blocking their admission.</summary>
public sealed class BackgroundWorkCoordinator(TimeSpan? quietPeriod = null)
{
    private readonly object _sync = new();
    private readonly TimeSpan _quietPeriod = quietPeriod ?? TimeSpan.FromSeconds(2);
    private DateTime _lastForeground = DateTime.UtcNow;
    private int _foreground;
    private BackgroundLease? _background;

    public IDisposable EnterForeground()
    {
        BackgroundLease? background;
        lock (_sync) { _foreground++; _lastForeground = DateTime.UtcNow; background = _background; }
        background?.Cancel();
        return new ForegroundLease(this);
    }

    public BackgroundLease? TryEnterBackground(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_foreground != 0 || _background is not null || DateTime.UtcNow - _lastForeground < _quietPeriod) return null;
            return _background = new BackgroundLease(this, token);
        }
    }

    public sealed class BackgroundLease : IDisposable
    {
        private readonly BackgroundWorkCoordinator _owner;
        private readonly CancellationTokenSource _stop;
        private int _disposed;
        internal BackgroundLease(BackgroundWorkCoordinator owner, CancellationToken token)
        { _owner = owner; _stop = CancellationTokenSource.CreateLinkedTokenSource(token); }
        public CancellationToken Token => _stop.Token;
        internal void Cancel() { try { _stop.Cancel(); } catch (ObjectDisposedException) { } }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_owner._sync) if (ReferenceEquals(_owner._background, this)) _owner._background = null;
            _stop.Dispose();
        }
    }

    private sealed class ForegroundLease(BackgroundWorkCoordinator owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._sync) { owner._foreground--; owner._lastForeground = DateTime.UtcNow; }
        }
    }
}
