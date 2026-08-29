using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace Picer.Core.Vision;

public sealed class FaceThumbnailService
{
    private const int CacheVersion = 1;
    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _workers = new(2, 2);

    public FaceThumbnailService(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
        Directory.CreateDirectory(cacheDirectory);
    }

    public async Task<string?> GetOrCreateAsync(UnassignedFaceRecord face, CancellationToken cancellationToken = default)
    {
        var destination = Path.Combine(_cacheDirectory, $"{face.FaceId}.v{CacheVersion}.jpg");
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

    private static string? Create(UnassignedFaceRecord face, string destination, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var image = Image.Load(face.ImagePath);
            var paddingX = face.Box.Width * 0.25f;
            var paddingY = face.Box.Height * 0.25f;
            var left = Math.Clamp(face.Box.X - paddingX, 0, 1);
            var top = Math.Clamp(face.Box.Y - paddingY, 0, 1);
            var right = Math.Clamp(face.Box.X + face.Box.Width + paddingX, 0, 1);
            var bottom = Math.Clamp(face.Box.Y + face.Box.Height + paddingY, 0, 1);
            var rectangle = new Rectangle(
                Math.Clamp((int)(left * image.Width), 0, image.Width - 1),
                Math.Clamp((int)(top * image.Height), 0, image.Height - 1),
                Math.Max(1, (int)((right - left) * image.Width)),
                Math.Max(1, (int)((bottom - top) * image.Height)));
            rectangle.Width = Math.Min(rectangle.Width, image.Width - rectangle.X);
            rectangle.Height = Math.Min(rectangle.Height, image.Height - rectangle.Y);
            image.Mutate(context => context
                .Crop(rectangle)
                .Resize(new ResizeOptions { Mode = ResizeMode.Crop, Size = new Size(160, 160) }));
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
