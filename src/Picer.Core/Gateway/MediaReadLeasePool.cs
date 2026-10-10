namespace Picer.Core.Gateway;

public sealed class MediaSourceChangedException : IOException
{
    public MediaSourceChangedException() : base("media_source_changed") { }
}

/// <summary>Opaque versions backed by an immutable open handle, including volumes without readable NTFS USN.</summary>
public sealed class MediaReadLeasePool : IDisposable
{
    private sealed class Entry(string path, FileStream keeper)
    {
        public string Path { get; } = path;
        public FileStream Keeper { get; } = keeper;
        public int Readers;
        public bool Released;
        public DateTime Used = DateTime.UtcNow;
    }
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Timer _expiry;
    private bool _disposed;
    public MediaReadLeasePool() => _expiry = new(_ => Sweep(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    public sealed class Lease(string etag, Action release) : IDisposable
    {
        private Action? _release = release;
        public string ETag { get; } = etag;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
    public Lease Acquire(string path, string? expected = null)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SweepLocked();
            Entry entry;
            string tag;
            if (expected is not null)
            {
                tag = expected;
                if (!_entries.TryGetValue(tag, out entry!) || entry.Released || !entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) throw new MediaSourceChangedException();
            }
            else
            {
                if (_entries.Count >= 32) throw new IOException("media_reader_capacity");
                tag = "\"lease-" + Guid.NewGuid().ToString("N") + "\"";
                entry = new(path, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess));
                _entries.Add(tag, entry);
            }
            entry.Readers++; entry.Used = DateTime.UtcNow;
            return new(tag, () =>
            {
                lock (_sync)
                {
                    entry.Readers--; entry.Used = DateTime.UtcNow;
                    if (entry.Readers == 0 && entry.Released) Remove(tag, entry);
                }
            });
        }
    }
    public void Touch(string tag) { lock (_sync) if (_entries.TryGetValue(tag, out var entry) && !entry.Released) entry.Used = DateTime.UtcNow; }
    public void Release(string tag)
    { lock (_sync) if (_entries.TryGetValue(tag, out var entry)) { entry.Released = true; if (entry.Readers == 0) Remove(tag, entry); } }
    private void Remove(string tag, Entry entry) { _entries.Remove(tag); entry.Keeper.Dispose(); }
    private void Sweep() { lock (_sync) SweepLocked(); }
    private void SweepLocked()
    {
        foreach (var pair in _entries.Where(pair => pair.Value.Readers == 0 && (pair.Value.Released || DateTime.UtcNow - pair.Value.Used > TimeSpan.FromMinutes(2))).ToArray()) Remove(pair.Key, pair.Value);
    }
    public void Clear()
    { lock (_sync) foreach (var pair in _entries.ToArray()) { pair.Value.Released = true; if (pair.Value.Readers == 0) Remove(pair.Key, pair.Value); } }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _expiry.Dispose();
            Clear();
        }
    }
}
