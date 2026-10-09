using System.Buffers;
using System.Security.Cryptography;

namespace Picer.Core.Gateway;

/// <summary>Content-addressed cache with bounded downloads and an in-memory LRU inventory.</summary>
public sealed class RemoteAssetCache
{
    private sealed class ContentGate { public readonly SemaphoreSlim Semaphore = new(1, 1); public int Users; }
    private sealed record CacheEntry(string Path, long Size);
    private readonly string _root;
    private readonly long _maxBytes;
    private readonly SemaphoreSlim _downloads;
    private readonly SemaphoreSlim _capacity = new(1, 1);
    private readonly object _contentLock = new();
    private readonly Dictionary<string, ContentGate> _contentGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _inventoryLock = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CacheEntry> _lru = new();
    private long _bytes;
    private long _generation;

    public RemoteAssetCache(string root, long maxBytes = 20L * 1024 * 1024 * 1024, int maximumConcurrentDownloads = 4)
    {
        _root = Path.GetFullPath(root);
        _maxBytes = maxBytes > 0 ? maxBytes : throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (maximumConcurrentDownloads is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(maximumConcurrentDownloads));
        _downloads = new(maximumConcurrentDownloads, maximumConcurrentDownloads);
        Directory.CreateDirectory(_root);
        // Inventory once, not a full filesystem walk after every downloaded thumbnail.
        foreach (var file in Directory.EnumerateFiles(_root).Select(path => new FileInfo(path)).OrderBy(file => file.LastAccessTimeUtc))
        {
            if (file.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            { if (file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) TryDelete(file.FullName); continue; }
            if (file.Name.Length != 64 || !file.Name.All(Uri.IsHexDigit)) continue;
            AddInventory(file.FullName, file.Length);
        }
    }

    public async Task<string> GetOrDownloadAsync(string sha256, Func<Stream> openSource, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = NormalizeHash(sha256);
        if (TryGetCached(normalized) is { } cached) return cached;
        ContentGate content;
        lock (_contentLock)
        {
            if (!_contentGates.TryGetValue(normalized, out content!)) _contentGates.Add(normalized, content = new());
            content.Users++;
        }
        var entered = false;
        try
        {
            await content.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false); entered = true;
            if (TryGetCached(normalized) is { } existing) return existing;
            await _downloads.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var generation = Interlocked.Read(ref _generation);
                var destination = Path.Combine(_root, normalized);
                var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    await using (var source = openSource())
                    await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                        await CopyBoundedAsync(source, target, cancellationToken).ConfigureAwait(false);
                    string actual;
                    await using (var verify = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                        actual = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
                    if (!string.Equals(actual, normalized, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("远程资源 SHA-256 校验失败。");
                    await _capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var length = new FileInfo(temporary).Length;
                        await EnsureCapacityAsync(length, generation, cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        lock (_inventoryLock)
                        {
                            if (generation != Interlocked.Read(ref _generation)) throw new OperationCanceledException("缓存已清理，请重新请求资源。");
                            if (_bytes + length > _maxBytes) throw new IOException("远程缓存空间已变化，请重试。");
                            File.Move(temporary, destination, overwrite: true);
                            AddInventory(destination, length);
                        }
                        return destination;
                    }
                    finally { _capacity.Release(); }
                }
                finally { TryDelete(temporary); }
            }
            finally { _downloads.Release(); }
        }
        finally
        {
            if (entered) content.Semaphore.Release();
            lock (_contentLock)
                if (--content.Users == 0) { _contentGates.Remove(normalized); content.Semaphore.Dispose(); }
        }
    }

    private async Task CopyBoundedAsync(Stream source, Stream destination, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long length = 0;
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                length += read;
                if (length > _maxBytes) throw new IOException("资源超过远程缓存容量。");
                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public string? TryGetCached(string sha256)
    {
        var path = Path.Combine(_root, NormalizeHash(sha256));
        lock (_inventoryLock)
        {
            if (!File.Exists(path)) { RemoveInventory(path); return null; }
            if (_entries.TryGetValue(path, out var entry)) { _lru.Remove(entry); _lru.AddLast(entry); }
            else AddInventory(path, new FileInfo(path).Length);
        }
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return path;
    }

    public void Clear()
    {
        lock (_inventoryLock)
        {
            Interlocked.Increment(ref _generation);
            foreach (var path in _entries.Keys.ToArray()) if (TryDelete(path)) RemoveInventory(path);
        }
    }

    private async Task EnsureCapacityAsync(long incoming, long generation, CancellationToken token)
    {
        var examined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var deleted = 0;
            lock (_inventoryLock)
            {
                if (generation != Interlocked.Read(ref _generation)) throw new OperationCanceledException("缓存已清理，请重新请求资源。");
                if (_bytes + incoming <= _maxBytes) return;
                var node = _lru.First;
                while (node is not null && deleted < 64 && _bytes + incoming > _maxBytes)
                {
                    var next = node.Next;
                    if (examined.Add(node.Value.Path))
                    {
                        deleted++;
                        if (TryDelete(node.Value.Path)) RemoveInventory(node.Value.Path);
                    }
                    node = next;
                }
                if (_bytes + incoming <= _maxBytes) return;
                if (deleted == 0) throw new IOException("远程缓存空间不足或缓存文件正在使用。");
            }
            await Task.Yield();
        }
    }

    private void AddInventory(string path, long length)
    {
        RemoveInventory(path);
        _entries[path] = _lru.AddLast(new CacheEntry(path, length));
        _bytes += length;
    }
    private void RemoveInventory(string path)
    {
        if (!_entries.Remove(path, out var node)) return;
        _bytes -= node.Value.Size; _lru.Remove(node);
    }
    private static string NormalizeHash(string hash)
    {
        var normalized = hash.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(value => !Uri.IsHexDigit(value))) throw new ArgumentException("资源哈希必须是 SHA-256。", nameof(hash));
        return normalized;
    }
    private static bool TryDelete(string path)
    { try { File.Delete(path); return true; } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; } }
}
