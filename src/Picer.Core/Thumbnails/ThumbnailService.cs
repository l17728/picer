using Picer.Core.Imaging;

namespace Picer.Core.Thumbnails;

public sealed class ThumbnailService
{
    private readonly string _cacheDir;
    private readonly int _maxSize;
    private const int CacheVersion = 1;
    private static readonly object[] ImageLocks = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();

    public ThumbnailService(string cacheDir, int maxSize = 256)
    {
        _cacheDir = cacheDir;
        _maxSize = maxSize;
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

    public void Delete(long imageId)
    {
        foreach (var path in Directory.EnumerateFiles(_cacheDir, $"{imageId}.*")) File.Delete(path);
    }

    private static void SaveAtomic(RasterImage image, string destination)
    {
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { image.SaveAsJpeg(temp); File.Move(temp, destination, true); }
        finally { CleanupFailed(temp); }
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
}
