using Picer.Core.Data;
using Picer.Core.Imaging;
using Picer.Core.Indexing;
using Picer.Core.Thumbnails;
using Picer.Tests.Stubs;

namespace Picer.Tests;

public sealed class IndexedImageHashTests
{
    [Fact]
    public async Task Indexing_StoresAll64DifferenceHashBits()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-indexed-hash-" + Guid.NewGuid().ToString("N"));
        var photos = Path.Combine(root, "photos");
        Directory.CreateDirectory(photos);
        try
        {
            using (var image = new RasterImage(80, 40))
            {
                for (var y = 0; y < image.Height; y++)
                for (var x = 0; x < image.Width; x++)
                {
                    var level = (byte)(255 - x * 3);
                    image[x, y] = new RgbPixel(level, level, level);
                }
                image.SaveAsPng(Path.Combine(photos, "gradient.png"));
            }
            using var database = new PicerDatabase(":memory:");
            var source = database.AddSource(photos);
            var engine = new IndexEngine(database, new DiskBackedStorageProvider(),
                new ThumbnailService(Path.Combine(root, "thumbs")));
            await engine.IndexSourceAsync(source);
            var record = Assert.Single(database.GetImages(source.Id));
            Assert.Equal(-1L, record.DifferenceHash);
            Assert.Equal(80, record.Width);
            Assert.NotNull(record.ThumbPath);
            Assert.True(File.Exists(record.ThumbPath));
        }
        finally { Directory.Delete(root, true); }
    }
}
