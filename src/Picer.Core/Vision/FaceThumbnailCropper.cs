using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace Picer.Core.Vision;

public static class FaceThumbnailCropper
{
    public static Stream? Crop(Stream source, NormalizedFaceBox box, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var image = Image.Load(source);
            var paddingX = box.Width * .25f; var paddingY = box.Height * .25f;
            var left = Math.Clamp(box.X - paddingX, 0, 1); var top = Math.Clamp(box.Y - paddingY, 0, 1);
            var right = Math.Clamp(box.X + box.Width + paddingX, 0, 1); var bottom = Math.Clamp(box.Y + box.Height + paddingY, 0, 1);
            var rectangle = new Rectangle(Math.Clamp((int)(left * image.Width), 0, image.Width - 1), Math.Clamp((int)(top * image.Height), 0, image.Height - 1), Math.Max(1, (int)((right - left) * image.Width)), Math.Max(1, (int)((bottom - top) * image.Height)));
            rectangle.Width = Math.Min(rectangle.Width, image.Width - rectangle.X); rectangle.Height = Math.Min(rectangle.Height, image.Height - rectangle.Y);
            image.Mutate(context => context.Crop(rectangle).Resize(new ResizeOptions { Mode = ResizeMode.Crop, Size = new Size(160, 160) }));
            var output = new MemoryStream(); image.SaveAsJpeg(output); output.Position = 0; return output;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}
