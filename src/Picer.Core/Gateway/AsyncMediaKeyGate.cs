namespace Picer.Core.Gateway;

/// <summary>Serializes the same media key; completed keys retain no locks or identities.</summary>
internal sealed class AsyncMediaKeyGate
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private sealed class Entry { public readonly SemaphoreSlim Gate = new(1, 1); public int Users; }

    public async Task<IDisposable> EnterAsync(string key, CancellationToken token)
    {
        Entry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out entry!)) _entries[key] = entry = new();
            entry.Users++;
        }
        try { await entry.Gate.WaitAsync(token).ConfigureAwait(false); }
        catch { Release(key, entry, false); throw; }
        return new Lease(this, key, entry);
    }

    private void Release(string key, Entry entry, bool entered)
    {
        if (entered) entry.Gate.Release();
        lock (_sync)
            if (--entry.Users == 0) { _entries.Remove(key); entry.Gate.Dispose(); }
    }

    private sealed class Lease(AsyncMediaKeyGate owner, string key, Entry entry) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(key, entry, true); }
    }
}
