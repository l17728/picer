using System.IO;
using System.Windows.Media.Imaging;

namespace Picer.App.Services;

internal static class ThumbnailBitmapLoader
{
    public static async Task<BitmapImage?> LoadRemoteAsync(Picer.Core.Gateway.RemoteThumbnailCache cache,
        Picer.Core.Models.ImageRecord record, Func<CancellationToken, Task<Stream?>> source,
        CancellationToken token, bool prefetch = false)
    {
        var path = cache.TryGet(record) ?? await cache.GetOrDownloadAsync(record, source, token, prefetch);
        if (path is null) return null;
        try { return await Task.Run(() => Load(path), token); }
        catch (OperationCanceledException) { throw; }
        catch { cache.Invalidate(record); throw; }
    }
    public static BitmapImage Load(string path)
    {
        // Own the stream explicitly: a failed WPF URI decode can otherwise
        // retain a file handle and prevent invalidating the corrupt cache.
        using var source = File.OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 256;
        image.StreamSource = source;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
