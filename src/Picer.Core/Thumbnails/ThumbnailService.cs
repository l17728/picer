using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

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

    public string? GetOrCreate(string sourcePath, long imageId)
    {
        var dest = GetCachePath(imageId);
        if (File.Exists(dest))
            return dest;

        lock (ImageLocks[(int)(Math.Abs(imageId) % ImageLocks.Length)])
        {
            if (File.Exists(dest)) return dest;
            try
            {
                using var image = Image.Load(sourcePath);
                image.Mutate(ctx => ctx.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(_maxSize, _maxSize) }));
                SaveAtomic(image, dest);
                return dest;
            }
            catch { CleanupFailed(dest); return null; }
        }
    }

    public string? GetOrCreate(Stream stream, long imageId)
    {
        var dest = GetCachePath(imageId);
        if (File.Exists(dest))
            return dest;

        lock (ImageLocks[(int)(Math.Abs(imageId) % ImageLocks.Length)])
        {
            if (File.Exists(dest)) return dest;
            try
            {
                using var image = Image.Load(stream);
                image.Mutate(ctx => ctx.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(_maxSize, _maxSize) }));
                SaveAtomic(image, dest);
                return dest;
            }
            catch { CleanupFailed(dest); return null; }
        }
    }

    public string? GetOrCreateFromImage(Image image, long imageId)
    {
        var dest = GetCachePath(imageId);
        if (File.Exists(dest))
            return dest;

        lock (ImageLocks[(int)(Math.Abs(imageId) % ImageLocks.Length)])
        {
            if (File.Exists(dest)) return dest;
            try
            {
                using var resized = image.Clone(ctx => ctx.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(_maxSize, _maxSize) }));
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

    private static void SaveAtomic(Image image, string destination)
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
