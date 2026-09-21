using System.Security.Cryptography;

namespace Picer.Core.Gateway;

/// <summary>Content-addressed cache for remote thumbnails, photos and videos.</summary>
public sealed class RemoteAssetCache
{
    private readonly string _root;
    private readonly long _maxBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RemoteAssetCache(string root, long maxBytes = 20L * 1024 * 1024 * 1024)
    {
        _root = Path.GetFullPath(root);
        _maxBytes = maxBytes > 0 ? maxBytes : throw new ArgumentOutOfRangeException(nameof(maxBytes));
        Directory.CreateDirectory(_root);
        CleanupPartials();
    }

    public async Task<string> GetOrDownloadAsync(string sha256, Func<Stream> openSource, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeHash(sha256);
        var destination = Path.Combine(_root, normalized);
        if (File.Exists(destination))
        {
            Touch(destination);
            return destination;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(destination)) { Touch(destination); return destination; }
            var temporary = destination + ".part";
            try
            {
                await using (var source = openSource())
                await using (var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.SequentialScan))
                {
                    await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                }
                string actual;
                await using (var verify = File.OpenRead(temporary))
                    actual = Convert.ToHexString(SHA256.HashData(verify)).ToLowerInvariant();
                if (!string.Equals(actual, normalized, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("远程资源 SHA-256 校验失败。");
                EnsureCapacity(new FileInfo(temporary).Length);
                File.Move(temporary, destination, overwrite: true);
                return destination;
            }
            finally { TryDelete(temporary); }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Returns a cached content-addressed file without contacting the remote service.</summary>
    public string? TryGetCached(string sha256)
    {
        var destination = Path.Combine(_root, NormalizeHash(sha256));
        if (!File.Exists(destination)) return null;
        Touch(destination);
        return destination;
    }

    public void Clear() { foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.TopDirectoryOnly)) TryDelete(file); }

    private void EnsureCapacity(long incoming)
    {
        var files = Directory.EnumerateFiles(_root, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path)).ToList();
        var current = files.Sum(file => file.Length);
        foreach (var file in files.OrderBy(file => file.LastAccessTimeUtc))
        {
            if (current + incoming <= _maxBytes) return;
            current -= file.Length;
            TryDelete(file.FullName);
        }
        if (current + incoming > _maxBytes) throw new IOException("远程缓存空间不足。");
    }

    private void CleanupPartials()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*.part", SearchOption.TopDirectoryOnly)) TryDelete(file);
    }

    private static string NormalizeHash(string hash)
    {
        var normalized = hash.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(value => !Uri.IsHexDigit(value))) throw new ArgumentException("资源哈希必须是 SHA-256。", nameof(hash));
        return normalized;
    }

    private static void Touch(string path) { try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { } }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
