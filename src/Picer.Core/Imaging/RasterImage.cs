using ImageMagick;
using ImageMagick.Configuration;

namespace Picer.Core.Imaging;

public readonly record struct RgbPixel(byte R, byte G, byte B, byte A = 255);

/// <summary>Owned raster pixels. Paths are opened as files, never interpreted as ImageMagick commands.</summary>
public sealed class RasterImage : IDisposable
{
    private readonly IMagickImage<byte> _image;

    static RasterImage()
    {
        // This application reads raster media only; decoding must never invoke
        // a delegate, read an indirect path, or fetch a remote resource.
        var configuration = ConfigurationFiles.Default;
        configuration.Policy.Data = """
            <policymap>
              <policy domain="delegate" rights="none" pattern="*" />
              <policy domain="filter" rights="none" pattern="*" />
              <policy domain="path" rights="none" pattern="@*" />
              <policy domain="coder" rights="none" pattern="*" />
              <policy domain="coder" rights="read|write" pattern="{BMP,GIF,ICO,JPEG,PNG,TIFF,WEBP,XC}" />
              <policy domain="resource" name="width" value="32KP" />
              <policy domain="resource" name="height" value="32KP" />
              <policy domain="resource" name="memory" value="256MiB" />
              <policy domain="resource" name="map" value="512MiB" />
              <policy domain="resource" name="disk" value="1GiB" />
              <policy domain="resource" name="thread" value="2" />
            </policymap>
            """;
        MagickNET.Initialize(configuration);
    }

    private RasterImage(IMagickImage<byte> image)
    {
        _image = image;
        _image.ColorType = ColorType.TrueColorAlpha;
    }
    public RasterImage(int width, int height) : this(width, height, new RgbPixel(0, 0, 0)) { }
    public RasterImage(int width, int height, RgbPixel color)
        : this(new MagickImage(new MagickColor(color.R, color.G, color.B, color.A),
            checked((uint)width), checked((uint)height))) { }

    public int Width => checked((int)_image.Width);
    public int Height => checked((int)_image.Height);

    public RgbPixel this[int x, int y]
    {
        get
        {
            using var pixels = _image.GetPixels();
            var color = pixels.GetPixel(x, y).ToColor()!;
            return new(color.R, color.G, color.B, color.A);
        }
        set
        {
            using var pixels = _image.GetPixels();
            pixels.SetPixel(x, y, new byte[] { value.R, value.G, value.B, value.A });
        }
    }

    public static RasterImage Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static RasterImage Load(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return Load(stream);
    }

    public static RasterImage Load(Stream stream)
    {
        try
        {
            var image = new MagickImage(stream, new MagickReadSettings { FrameIndex = 0, FrameCount = 1 });
            try
            {
                // Normalize channel access for grayscale and CMYK files.
                image.ColorSpace = ColorSpace.sRGB;
                return new RasterImage(image);
            }
            catch { image.Dispose(); throw; }
        }
        catch (MagickException exception) { throw new InvalidDataException("无法解码图片。", exception); }
    }

    public static Task<RasterImage> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => LoadWithCancellation(() => Load(path), cancellationToken), cancellationToken);

    public static Task<RasterImage> LoadAsync(Stream stream, CancellationToken cancellationToken = default) =>
        Task.Run(() => LoadWithCancellation(() => Load(stream), cancellationToken), cancellationToken);

    private static RasterImage LoadWithCancellation(Func<RasterImage> load, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var image = load();
        if (cancellationToken.IsCancellationRequested) { image.Dispose(); cancellationToken.ThrowIfCancellationRequested(); }
        return image;
    }

    public static (int Width, int Height) Identify(Stream stream)
    {
        var info = new MagickImageInfo(stream);
        return (checked((int)info.Width), checked((int)info.Height));
    }

    public RasterImage Clone() => new(_image.Clone());

    public RasterImage Resize(int width, int height)
    {
        _image.FilterType = FilterType.Catrom;
        _image.Resize(new MagickGeometry(checked((uint)width), checked((uint)height)) { IgnoreAspectRatio = true });
        return this;
    }

    public RasterImage ResizeToFit(int width, int height)
    {
        var scale = Math.Min((double)width / Width, (double)height / Height);
        return Resize(Math.Max(1, (int)Math.Round(Width * scale)), Math.Max(1, (int)Math.Round(Height * scale)));
    }

    public RasterImage ResizeToFill(int width, int height)
    {
        var scale = Math.Max((double)width / Width, (double)height / Height);
        Resize(Math.Max(width, (int)Math.Round(Width * scale)), Math.Max(height, (int)Math.Round(Height * scale)));
        return Crop((Width - width) / 2, (Height - height) / 2, width, height);
    }

    public RasterImage ResizePadded(int width, int height)
    {
        ResizeToFit(width, height);
        _image.BackgroundColor = MagickColors.Black;
        _image.Extent(checked((uint)width), checked((uint)height), Gravity.Center);
        _image.ResetPage();
        return this;
    }

    public RasterImage Crop(int x, int y, int width, int height)
    {
        _image.Crop(new MagickGeometry(x, y, checked((uint)width), checked((uint)height)));
        _image.ResetPage();
        return this;
    }

    public void Draw(RasterImage source, int x, int y) => _image.Composite(source._image, x, y, CompositeOperator.Over);

    public byte[] CopyRgbPixels()
    {
        using var pixels = _image.GetPixels();
        return pixels.ToByteArray(PixelMapping.RGB)!;
    }

    public void StripMetadata() => _image.Strip();

    public void SaveAsJpeg(string path, int quality = 75)
    {
        using var output = File.Create(path);
        SaveAsJpeg(output, quality);
    }

    public void SaveAsJpeg(Stream stream, int quality = 75)
    {
        _image.Quality = checked((uint)quality);
        _image.Write(stream, MagickFormat.Jpeg);
    }

    public Task SaveAsJpegAsync(string path, int quality = 75, CancellationToken cancellationToken = default) =>
        Task.Run(() => { cancellationToken.ThrowIfCancellationRequested(); SaveAsJpeg(path, quality); cancellationToken.ThrowIfCancellationRequested(); }, cancellationToken);

    public Task SaveAsJpegAsync(Stream stream, int quality = 75, CancellationToken cancellationToken = default) =>
        Task.Run(() => { cancellationToken.ThrowIfCancellationRequested(); SaveAsJpeg(stream, quality); cancellationToken.ThrowIfCancellationRequested(); }, cancellationToken);

    public void SaveAsPng(string path)
    {
        using var output = File.Create(path);
        SaveAsPng(output);
    }

    public void SaveAsPng(Stream stream) => _image.Write(stream, MagickFormat.Png);

    public void Save(string path)
    {
        var format = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => MagickFormat.Jpeg,
            ".png" => MagickFormat.Png,
            ".webp" => MagickFormat.WebP,
            ".tif" or ".tiff" => MagickFormat.Tiff,
            ".gif" => MagickFormat.Gif,
            ".bmp" => MagickFormat.Bmp,
            _ => throw new NotSupportedException("不支持的图片输出格式。")
        };
        using var output = File.Create(path);
        _image.Write(output, format);
    }

    public void Dispose() => _image.Dispose();
}
