using System.Security.Cryptography;
using Picer.Core.Video;

namespace Picer.Core.Gateway;

/// <summary>Independent bounded downloads, per-key coalescing and short atomic publication.</summary>
public sealed class PlayableMediaCache
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, WeakReference<PlayableMediaCache>> Instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;
    private readonly long _limit;
    private readonly object _inventory = new(), _purge = new();
    private readonly AsyncMediaKeyGate _keys = new();
    private readonly SemaphoreSlim _downloads = new(4, 4);
    private readonly Dictionary<string, (long Size, DateTime Access)> _files = new(StringComparer.OrdinalIgnoreCase);
    private long _bytes;
    public PlayableMediaCache(string root, long limit = 20L * 1024 * 1024 * 1024)
    {
        _root = Path.GetFullPath(root); _limit = limit > 0 ? limit : throw new ArgumentOutOfRangeException(nameof(limit));
        Directory.CreateDirectory(_root);
        Instances[_root] = new(this);
        foreach (var path in Directory.EnumerateFiles(_root))
        {
            var file = new FileInfo(path);
            var stem = Path.GetFileNameWithoutExtension(path);
            if (stem.Length == 64 && stem.All(Uri.IsHexDigit)) { _files[path] = (file.Length, file.LastAccessTimeUtc); _bytes += file.Length; }
        }
    }
    public async Task<string> GetAsync(string key, string extension, string? sha256, Func<string, CancellationToken, Task> download, CancellationToken token)
    {
        if (key.Length != 64 || !key.All(Uri.IsHexDigit)) throw new ArgumentException("invalid_cache_key");
        if (extension.Length is < 2 or > 12 || extension[0] != '.' || !extension[1..].All(char.IsAsciiLetterOrDigit)) extension = ".bin";
        var destination = Path.Combine(_root, key.ToLowerInvariant() + extension.ToLowerInvariant());
        using var operation = VideoOperationLifetime.Begin(_root, token);
        token = operation.Token;
        if (TryGet(destination)) return destination;
        using var keyLease = await _keys.EnterAsync(destination, token).ConfigureAwait(false);
        if (TryGet(destination)) return destination;
        await _downloads.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await Task.Run(() => download(temporary, token), token).ConfigureAwait(false);
                var size = new FileInfo(temporary).Length;
                if (size > _limit) throw new IOException("媒体超过本地缓存容量。");
                if (sha256 is { Length: 64 })
                {
                    await using var source = File.OpenRead(temporary);
                    if (!Convert.ToHexString(await SHA256.HashDataAsync(source, token).ConfigureAwait(false)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("原文件与索引哈希不一致，请重新扫描来源。");
                }
                lock (_inventory)
                {
                    foreach (var entry in _files.OrderBy(pair => pair.Value.Access).ToArray())
                    {
                        if (_bytes + size <= _limit) break;
                        try { File.Delete(entry.Key); _files.Remove(entry.Key); _bytes -= entry.Value.Size; }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                    if (_bytes + size > _limit) throw new IOException("媒体缓存已满，部分文件正在播放。");
                    token.ThrowIfCancellationRequested();
                    File.Move(temporary, destination, true);
                    _files[destination] = (size, DateTime.UtcNow); _bytes += size;
                }
                return destination;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _downloads.Release(); }
    }
    private bool TryGet(string destination)
    {
        lock (_inventory)
        {
            if (!_files.TryGetValue(destination, out var entry)) return false;
            if (!File.Exists(destination)) { _files.Remove(destination); _bytes -= entry.Size; return false; }
            _files[destination] = (entry.Size, DateTime.UtcNow); return true;
        }
    }
    public static void Purge(string dataRoot)
    {
        var root = Path.GetFullPath(Path.Combine(dataRoot, "remote-assets", "playable"));
        if (Instances.TryGetValue(root, out var reference) && reference.TryGetTarget(out var cache))
        {
            lock (cache._purge)
            {
                using var purge = VideoOperationLifetime.BeginPurgeAsync(root, CancellationToken.None).GetAwaiter().GetResult();
                lock (cache._inventory)
                {
                    // Preserve inventory when deletion fails (for example a playing file).
                    DeleteOwned(root);
                    cache._files.Clear(); cache._bytes = 0;
                }
            }
        }
        else DeleteOwned(root);
    }
    private static void DeleteOwned(string root)
    {
        if (!Directory.Exists(root)) return;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("playable_cache_link_rejected");
        foreach (var path in Directory.EnumerateFiles(root))
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            if (stem.Length == 64 && stem.All(Uri.IsHexDigit)) File.Delete(path);
        }
    }
}
