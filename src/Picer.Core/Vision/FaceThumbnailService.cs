using Picer.Core.Imaging;

namespace Picer.Core.Vision;

public sealed class FaceThumbnailService
{
    private const int CacheVersion = 1;
    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _workers = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 4));

    public FaceThumbnailService(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
        Directory.CreateDirectory(cacheDirectory);
    }

    public async Task<string?> GetOrCreateAsync(UnassignedFaceRecord face, CancellationToken cancellationToken = default)
    {
        var destination = GetCachePath(face.FaceId);
        if (File.Exists(destination)) return destination;
        await _workers.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(destination)) return destination;
            return await Task.Run(() => Create(face, destination, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _workers.Release();
        }
    }

    public string? GetCachedPath(long faceId)
    {
        var path = GetCachePath(faceId);
        return File.Exists(path) ? path : null;
    }

    public async Task<string?> StoreAsync(long faceId, Stream content, CancellationToken cancellationToken = default)
    {
        var destination = GetCachePath(faceId);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = File.Create(temporary)) await content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, true);
            return destination;
        }
        catch (OperationCanceledException) { throw; }
        catch { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } return null; }
    }

    private string GetCachePath(long faceId) => Path.Combine(_cacheDirectory, $"{faceId}.v{CacheVersion}.jpg");

    private static string? Create(UnassignedFaceRecord face, string destination, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = !string.IsNullOrWhiteSpace(face.ThumbnailPath) && File.Exists(face.ThumbnailPath)
                ? face.ThumbnailPath
                : face.ImagePath;
            using var image = RasterImage.Load(source);
            var paddingX = face.Box.Width * 0.25f;
            var paddingY = face.Box.Height * 0.25f;
            var left = Math.Clamp(face.Box.X - paddingX, 0, 1);
            var top = Math.Clamp(face.Box.Y - paddingY, 0, 1);
            var right = Math.Clamp(face.Box.X + face.Box.Width + paddingX, 0, 1);
            var bottom = Math.Clamp(face.Box.Y + face.Box.Height + paddingY, 0, 1);
            var rectangle = new System.Drawing.Rectangle(
                Math.Clamp((int)(left * image.Width), 0, image.Width - 1),
                Math.Clamp((int)(top * image.Height), 0, image.Height - 1),
                Math.Max(1, (int)((right - left) * image.Width)),
                Math.Max(1, (int)((bottom - top) * image.Height)));
            rectangle.Width = Math.Min(rectangle.Width, image.Width - rectangle.X);
            rectangle.Height = Math.Min(rectangle.Height, image.Height - rectangle.Y);
            image.Crop(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height).ResizeToFill(160, 160);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                image.SaveAsJpeg(temporary);
                File.Move(temporary, destination, true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
            return destination;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}
