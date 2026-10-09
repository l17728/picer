using Picer.Core.Hashing;
using Picer.Core.Thumbnails;
using Picer.Core.Imaging;
using ImageMagick;

namespace Picer.Tests;

public sealed class ImageCodecRegressionTests
{
    [Theory]
    [InlineData("png")]
    [InlineData("jpg")]
    [InlineData("webp")]
    [InlineData("tiff")]
    [InlineData("bmp")]
    [InlineData("gif")]
    public void ThumbnailAndHash_ReadCommonFormatsWithoutChangingOriginal(string extension)
    {
        var directory = Path.Combine(Path.GetTempPath(), "picer-codec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source." + extension);
            using (var image = new RasterImage(80, 40))
            {
                for (var y = 0; y < image.Height; y++)
                for (var x = 0; x < image.Width; x++)
                    image[x, y] = new RgbPixel((byte)(x * 3), (byte)(y * 6), 90);
                image.Save(source);
            }
            var original = File.ReadAllBytes(source);
            var thumbnails = new ThumbnailService(Path.Combine(directory, "thumbs"), 32);
            var thumbnail = thumbnails.GetOrCreate(source, 1);
            Assert.NotNull(thumbnail);
            using var decoded = RasterImage.Load(thumbnail!);
            Assert.Equal(32, decoded.Width);
            Assert.Equal(16, decoded.Height);
            using var first = File.OpenRead(source);
            using var second = File.OpenRead(source);
            Assert.Equal(PerceptualHasher.ComputeDifferenceHash(first), PerceptualHasher.ComputeDifferenceHash(second));
            Assert.Equal(original, File.ReadAllBytes(source));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void TiffGroup4_SmallImageCanBeEncodedAndReencoded()
    {
        using var initializeRasterConfiguration = new RasterImage(1, 1);
        using var original = new MagickImage(MagickColors.White, 1, 1);
        original.Settings.Compression = CompressionMethod.Group4;
        original.Depth = 1;
        using var encoded = new MemoryStream();
        original.Write(encoded, MagickFormat.Tiff);
        encoded.Position = 0;
        using var decoded = new MagickImage(encoded);
        using var reencoded = new MemoryStream();
        decoded.Write(reencoded, MagickFormat.Tiff);
        reencoded.Position = 0;
        using var result = RasterImage.Load(reencoded);
        Assert.Equal(1, result.Width);
        Assert.Equal(1, result.Height);
        Assert.Equal(new RgbPixel(255, 255, 255), result[0, 0]);
    }

    [Fact]
    public void CropPaddingAndRgbChannelOrder_ArePreserved()
    {
        using var image = new RasterImage(8, 4, new RgbPixel(180, 70, 20));
        using var cropped = image.Clone().Crop(2, 1, 4, 2).ResizeToFill(10, 10);
        Assert.Equal(10, cropped.Width);
        Assert.Equal(10, cropped.Height);
        Assert.Equal(new byte[] { 180, 70, 20 }, cropped.CopyRgbPixels()[..3]);
        image.ResizePadded(8, 8);
        Assert.Equal(new RgbPixel(0, 0, 0), image[0, 0]);
        Assert.Equal(new RgbPixel(180, 70, 20), image[4, 4]);
        using var canvas = new RasterImage(16, 8);
        canvas.Draw(image, 8, 0);
        Assert.Equal(new RgbPixel(180, 70, 20), canvas[12, 4]);
        Assert.Equal(new RgbPixel(0, 0, 0), canvas[4, 4]);
    }

    [Fact]
    public void StripMetadata_RemovesPrivateProfilesBeforeJpegUpload()
    {
        using var initializeRasterConfiguration = new RasterImage(1, 1);
        using var source = new MagickImage(MagickColors.Red, 4, 4);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Artist, "private-test-marker");
        source.SetProfile(exif);
        source.Comment = "private-test-comment";
        using var input = new MemoryStream(source.ToByteArray(MagickFormat.Png));
        using var image = RasterImage.Load(input);
        image.StripMetadata();
        using var output = new MemoryStream();
        image.SaveAsJpeg(output);
        output.Position = 0;
        using var result = new MagickImage(output);
        Assert.Null(result.GetExifProfile());
        Assert.True(string.IsNullOrEmpty(result.Comment));
    }

    [Fact]
    public void VectorDocument_IsRejectedByRasterPolicy()
    {
        var svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><rect width='1' height='1'/></svg>");
        Assert.Throws<InvalidDataException>(() => RasterImage.Load(svg));
    }

    [Fact]
    public async Task CanceledLoad_DoesNotConsumeCallerStream()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RasterImage.LoadAsync(stream, new CancellationToken(true)));
        Assert.Equal(0, stream.Position);
        Assert.True(stream.CanRead);
    }
}
