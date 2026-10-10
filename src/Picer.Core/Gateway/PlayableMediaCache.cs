using System.Security.Cryptography;

namespace Picer.Core.Gateway;

/// <summary>Atomic playable files, bounded disk inventory and serialized publication.</summary>
public sealed class PlayableMediaCache
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, WeakReference<PlayableMediaCache>> Instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;
    private readonly long _limit;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource _generation = new();
    private readonly Dictionary<string, (long Size, DateTime Access)> _files = new();
    public PlayableMediaCache(string root, long limit = 20L * 1024 * 1024 * 1024)
    {
        _root = Path.GetFullPath(root); _limit = limit;
        Directory.CreateDirectory(_root);
        Instances[_root] = new(this);
        foreach (var path in Directory.EnumerateFiles(_root))
        {
            var file = new FileInfo(path);
            var stem = Path.GetFileNameWithoutExtension(path);
            if (stem.Length == 64 && stem.All(Uri.IsHexDigit)) _files[path] = (file.Length, file.LastAccessTimeUtc);
        }
    }
    public async Task<string> GetAsync(string key, string extension, string? sha256, Func<string, CancellationToken, Task> download, CancellationToken token)
    {
        if (key.Length != 64 || !key.All(Uri.IsHexDigit)) throw new ArgumentException("invalid_cache_key");
        if (extension.Length is < 2 or > 12 || extension[0] != '.' || !extension[1..].All(char.IsAsciiLetterOrDigit)) extension = ".bin";
        var destination = Path.Combine(_root, key + extension.ToLowerInvariant());
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var generation = CancellationTokenSource.CreateLinkedTokenSource(token, _generation.Token);
            token = generation.Token;
            if (_files.ContainsKey(destination) && File.Exists(destination))
            { _files[destination] = (new FileInfo(destination).Length, DateTime.UtcNow); return destination; }
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
                foreach (var entry in _files.OrderBy(pair => pair.Value.Access).ToArray())
                {
                    if (_files.Values.Sum(item => item.Size) + size <= _limit) break;
                    try { File.Delete(entry.Key); _files.Remove(entry.Key); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                if (_files.Values.Sum(item => item.Size) + size > _limit) throw new IOException("媒体缓存已满，部分文件正在播放。");
                token.ThrowIfCancellationRequested();
                File.Move(temporary, destination, true);
                _files[destination] = (size, DateTime.UtcNow);
                return destination;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _gate.Release(); }
    }
    public static void Purge(string dataRoot)
    {
        var root = Path.GetFullPath(Path.Combine(dataRoot, "remote-assets", "playable"));
        if (Instances.TryGetValue(root, out var reference) && reference.TryGetTarget(out var cache))
        {
            cache._generation.Cancel();
            cache._gate.Wait();
            try
            {
                DeleteOwned(root);
                cache._files.Clear();
            }
            finally
            {
                cache._generation.Dispose();
                cache._generation = new();
                cache._gate.Release();
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
