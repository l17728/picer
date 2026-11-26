using Picer.Core.Imaging;
using Picer.Core.Security;

namespace Picer.Core.Thumbnails;

public sealed class ThumbnailService : IDisposable
{
    private readonly string _cacheDir;
    private readonly int _maxSize;
    private const int CacheVersion = 1;
    private readonly MediaFileCipher? _cipher;
    private static readonly object[] ImageLocks = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();

    public ThumbnailService(string cacheDir, int maxSize = 256, MediaFileCipher? cipher = null)
    {
        _cacheDir = cacheDir;
        _maxSize = maxSize;
        _cipher = cipher;
        Directory.CreateDirectory(cacheDir);
    }

    public string CacheDir => _cacheDir;

    public string GetCachePath(long imageId) => Path.Combine(_cacheDir, $"{imageId}.v{CacheVersion}.jpg");

    public string GetCachePath(long imageId, string? sha256) =>
        sha256 is { Length: 64 } && sha256.All(Uri.IsHexDigit)
            ? Path.Combine(_cacheDir, $"sha256-{sha256.ToLowerInvariant()}.s{_maxSize}.v{CacheVersion}.jpg")
            : GetCachePath(imageId);

    public string? GetOrCreate(string sourcePath, long imageId, string? sha256 = null)
    {
        var dest = GetCachePath(imageId, sha256);
        if (File.Exists(dest))
            return dest;

        lock (ImageLocks[(int)(Math.Abs(imageId) % ImageLocks.Length)])
        {
            if (File.Exists(dest)) return dest;
            try
            {
                using var image = RasterImage.Load(sourcePath);
                image.ResizeToFit(_maxSize, _maxSize);
                SaveAtomic(image, dest);
                return dest;
            }
            catch { CleanupFailed(dest); return null; }
        }
    }

    public string? GetOrCreate(Stream stream, long imageId, string? sha256 = null)
    {
        var dest = GetCachePath(imageId, sha256);
        if (File.Exists(dest))
            return dest;

        lock (ImageLocks[(int)(Math.Abs(imageId) % ImageLocks.Length)])
        {
            if (File.Exists(dest)) return dest;
            try
            {
                using var image = RasterImage.Load(stream);
                image.ResizeToFit(_maxSize, _maxSize);
                SaveAtomic(image, dest);
                return dest;
            }
            catch { CleanupFailed(dest); return null; }
        }
    }

    public string? GetOrCreateFromImage(RasterImage image, long imageId, string? sha256 = null)
    {
        var dest = GetCachePath(imageId, sha256);
        if (File.Exists(dest))
            return dest;

        lock (ImageLocks[(int)(Math.Abs(imageId) % ImageLocks.Length)])
        {
            if (File.Exists(dest)) return dest;
            try
            {
                using var resized = image.Clone().ResizeToFit(_maxSize, _maxSize);
                SaveAtomic(resized, dest);
                return dest;
            }
            catch { CleanupFailed(dest); return null; }
        }
    }

    public Stream OpenRead(string path)
    {
        if (_cipher is null) return File.OpenRead(path);
        lock (GetPathLock(path))
        {
            var bytes = File.ReadAllBytes(path);
            var plaintext = _cipher.UnprotectOrReadLegacy(bytes, Path.GetFileName(path), out var wasEncrypted);
            if (!wasEncrypted) SaveProtectedAtomic(path, plaintext);
            return new MemoryStream(plaintext, writable: false);
        }
    }

    /// <summary>Encrypts legacy plaintext thumbnails already present when an encrypted cache is enabled.</summary>
    public (int Migrated, int Failed) ProtectExistingCacheFiles(CancellationToken cancellationToken = default)
    {
        if (_cipher is null) return (0, 0);
        var migrated = 0;
        var failed = 0;
        foreach (var path in Directory.EnumerateFiles(_cacheDir, "*.jpg", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stored = File.ReadAllBytes(path);
                if (_cipher.IsEncrypted(stored)) continue;
                using var _ = OpenRead(path);
                migrated++;
            }
            catch (OperationCanceledException) { throw; }
            catch { failed++; }
        }
        return (migrated, failed);
    }

    private static object GetPathLock(string path)
    {
        var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(Path.GetFullPath(path));
        return ImageLocks[(int)((uint)hash % (uint)ImageLocks.Length)];
    }

    public void Delete(long imageId)
    {
        foreach (var path in Directory.EnumerateFiles(_cacheDir, $"{imageId}.*")) File.Delete(path);
    }

    private void SaveAtomic(RasterImage image, string destination)
    {
        if (_cipher is null)
        {
            var clearTemp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { image.SaveAsJpeg(clearTemp); File.Move(clearTemp, destination, true); }
            finally { CleanupFailed(clearTemp); }
            return;
        }
        using var content = new MemoryStream();
        image.SaveAsJpeg(content);
        SaveProtectedAtomic(destination, content.GetBuffer().AsSpan(0, checked((int)content.Length)));
    }

    private void SaveProtectedAtomic(string destination, ReadOnlySpan<byte> plaintext)
    {
        var encrypted = _cipher!.Protect(plaintext, Path.GetFileName(destination));
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, encrypted); File.Move(temp, destination, true); }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(encrypted);
            CleanupFailed(temp);
        }
    }

    private static void CleanupFailed(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    public void Dispose() => _cipher?.Dispose();
}
