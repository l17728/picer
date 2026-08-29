using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Picer.Core.Storage;

public sealed class CachedStorageProvider : IStorageProvider
{
    private readonly IStorageProvider _inner;
    private readonly string _cacheDir;
    private readonly long _maxBytes;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    public CachedStorageProvider(IStorageProvider inner, string cacheDir, long maxBytes = 10L * 1024 * 1024 * 1024)
    {
        _inner = inner;
        _cacheDir = cacheDir;
        _maxBytes = maxBytes;
        Directory.CreateDirectory(cacheDir);
    }

    public string Scheme => _inner.Scheme;
    public IEnumerable<StorageFileInfo> EnumerateFiles(string root) => _inner.EnumerateFiles(root);
    public bool Exists(string key) => _inner.Exists(key);
    public StorageFileInfo? GetInfo(string key) => _inner.GetInfo(key);
    public Task<IEnumerable<StorageFileInfo>> EnumerateFilesAsync(string root, CancellationToken ct = default) => _inner.EnumerateFilesAsync(root, ct);
    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => _inner.ExistsAsync(key, ct);
    public Task<StorageFileInfo?> GetInfoAsync(string key, CancellationToken ct = default) => _inner.GetInfoAsync(key, ct);

    public Stream OpenRead(string key)
    {
        var path = CachePath(key, _inner.GetInfo(key));
        var gate = _gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            if (!File.Exists(path))
            {
                using var source = _inner.OpenRead(key);
                WriteAtomic(source, path);
                TrimCache(path);
            }
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            return File.OpenRead(path);
        }
        finally { gate.Release(); }
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var path = CachePath(key, await _inner.GetInfoAsync(key, ct).ConfigureAwait(false));
        var gate = _gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                using var source = await _inner.OpenReadAsync(key, ct).ConfigureAwait(false);
                await WriteAtomicAsync(source, path, ct).ConfigureAwait(false);
                TrimCache(path);
            }
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            return File.OpenRead(path);
        }
        finally { gate.Release(); }
    }

    private string CachePath(string key, StorageFileInfo? info)
    {
        var version = info is null ? "unknown" : $"{info.ETag ?? "no-etag"}:{info.Size}:{info.ModifiedUtc.Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key + "|" + version)));
        return Path.Combine(_cacheDir, hash + Path.GetExtension(key));
    }

    private static void WriteAtomic(Stream source, string path)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var target = File.Create(temp)) source.CopyTo(target); File.Move(temp, path, true); }
        finally { TryDelete(temp); }
    }

    private static async Task WriteAtomicAsync(Stream source, string path, CancellationToken ct)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var target = File.Create(temp)) await source.CopyToAsync(target, ct).ConfigureAwait(false);
            File.Move(temp, path, true);
        }
        finally { TryDelete(temp); }
    }

    private void TrimCache(string protectedPath)
    {
        var files = new DirectoryInfo(_cacheDir).EnumerateFiles().Where(file => !file.Name.EndsWith(".tmp"))
            .OrderByDescending(file => file.LastAccessTimeUtc).ToList();
        var total = files.Sum(file => file.Length);
        foreach (var file in files.AsEnumerable().Reverse())
        {
            if (total <= _maxBytes) break;
            if (string.Equals(file.FullName, protectedPath, StringComparison.OrdinalIgnoreCase)) continue;
            total -= file.Length;
            TryDelete(file.FullName);
        }
    }

    public void ClearCache() { foreach (var file in Directory.EnumerateFiles(_cacheDir)) TryDelete(file); }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
