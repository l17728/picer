using Picer.Core.Imaging;
using Picer.Core.Video;

namespace Picer.Tests;

public sealed class VideoKeyframeCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-frame-cache-" + Guid.NewGuid().ToString("N"));
    private string Target => Path.Combine(_root, "frame.jpg");

    [Fact]
    public async Task ColdDownload_ClosesWindowsHandleBeforePublishing()
    {
        var bytes = ImageBytes(1);
        using var source = new MemoryStream(bytes);
        await VideoKeyframeCache.WriteAsync(source, Target);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Target));
        using var exclusive = new FileStream(Target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Empty(Directory.GetFiles(_root, "*.partial-*"));
    }

    [Fact]
    public async Task EmptyDownload_PreservesPreviousCacheAndCleansTemporary()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(Target, [9]);
        using var source = new MemoryStream();
        await Assert.ThrowsAsync<IOException>(() => VideoKeyframeCache.WriteAsync(source, Target));
        Assert.Equal(new byte[] { 9 }, await File.ReadAllBytesAsync(Target));
        Assert.Empty(Directory.GetFiles(_root, "*.partial-*"));
    }

    [Fact]
    public async Task CanceledDownload_PreservesPreviousCacheAndCleansTemporary()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(Target, [9]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var source = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VideoKeyframeCache.WriteAsync(source, Target, cancellation.Token));
        Assert.Equal(new byte[] { 9 }, await File.ReadAllBytesAsync(Target));
        Assert.Empty(Directory.GetFiles(_root, "*.partial-*"));
    }

    [Fact]
    public async Task ConcurrentDownloads_PublishCompleteContentOnly()
    {
        await Task.WhenAll(Enumerable.Range(1, 16).Select(async value =>
        {
            using var source = new MemoryStream(ImageBytes((byte)value));
            await VideoKeyframeCache.WriteAsync(source, Target);
        }));
        var result = await File.ReadAllBytesAsync(Target);
        using var decoded = RasterImage.Load(result);
        Assert.Equal(4, decoded.Width);
        Assert.InRange(decoded[0, 0].R, (byte)1, (byte)16);
        Assert.Empty(Directory.GetFiles(_root, "*.partial-*"));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task CorruptImage_DoesNotReplaceValidCache()
    {
        var original = ImageBytes(42);
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(Target, original);
        using var invalid = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAnyAsync<Exception>(() => VideoKeyframeCache.WriteAsync(invalid, Target));
        Assert.Equal(original, await File.ReadAllBytesAsync(Target));
        Assert.Empty(Directory.GetFiles(_root, "*.partial-*"));
    }

    [Fact]
    public async Task ExistingCache_IsNotReplacedWhileBeingDisplayed()
    {
        Directory.CreateDirectory(_root);
        var original = ImageBytes(42);
        await File.WriteAllBytesAsync(Target, original);
        using var displayed = File.OpenRead(Target);
        using var download = new MemoryStream(ImageBytes(99));
        await VideoKeyframeCache.WriteAsync(download, Target);
        Assert.Equal(original, await File.ReadAllBytesAsync(Target));
        Assert.Empty(Directory.GetFiles(_root, "*.partial-*"));
    }

    private static byte[] ImageBytes(byte value)
    {
        using var image = new RasterImage(4, 3, new RgbPixel(value, 0, 0));
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }
}
