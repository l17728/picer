namespace Picer.Core.Gateway;

/// <summary>Bounded preview cache with independent active keys and an immediate hot-cache path.</summary>
public sealed class MediaPreviewCache(long capacity = 64L * 1024 * 1024)
{
    private readonly object _sync = new();
    private readonly AsyncMediaKeyGate _keys = new();
    private readonly Dictionary<string, (byte[] Bytes, DateTime Expires, LinkedListNode<string> Node)> _items = new();
    private readonly LinkedList<string> _lru = new();
    private long _bytes, _generation;
    public async Task<byte[]> GetAsync(string key, Func<CancellationToken, Task<byte[]>> factory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long generation;
        lock (_sync)
        {
            generation = _generation;
            if (TryGet(key) is { } cached) return cached;
        }
        using (await _keys.EnterAsync(key, token).ConfigureAwait(false))
        {
            lock (_sync)
            {
                if (generation != _generation) throw new OperationCanceledException("media_cache_cleared");
                if (TryGet(key) is { } cached) return cached;
            }
            var bytes = await factory(token).ConfigureAwait(false);
            if (bytes.LongLength > Math.Min(capacity, 8 * 1024 * 1024)) throw new IOException("media_preview_too_large");
            token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (generation != _generation) throw new OperationCanceledException("media_cache_cleared");
                while (_bytes + bytes.LongLength > capacity && _lru.First is { } first) Remove(first.Value);
                _items[key] = (bytes, DateTime.UtcNow.AddMinutes(5), _lru.AddLast(key));
                _bytes += bytes.LongLength;
            }
            return bytes;
        }
    }
    private byte[]? TryGet(string key)
    {
        if (!_items.TryGetValue(key, out var item)) return null;
        if (item.Expires <= DateTime.UtcNow) { Remove(key); return null; }
        _lru.Remove(item.Node); _lru.AddLast(item.Node); return item.Bytes;
    }
    private void Remove(string key)
    { if (_items.Remove(key, out var item)) { _bytes -= item.Bytes.LongLength; _lru.Remove(item.Node); } }
    public void Clear() { lock (_sync) { _generation++; _items.Clear(); _lru.Clear(); _bytes = 0; } }
}
