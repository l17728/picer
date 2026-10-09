using System.Net;
using System.Net.Http.Json;
using Picer.Core.Imaging;
using Picer.Core.Video;

namespace Picer.Tests;

public sealed class VideoProcessorCacheIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-processor-cache-" + Guid.NewGuid().ToString("N"));
    public VideoProcessorCacheIdentityTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private VideoRecord Record(string? hash) => new(7, 1, Path.Combine(_root, "source.mp4"), 3,
        new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), null, 0, 0, null, null, "active") { Sha256 = hash };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ServicePosterDoesNotReusePriorContentWithSameSizeAndTimestamp(bool knownHash)
    {
        var redPath = Path.Combine(_root, "red.jpg");
        var bluePath = Path.Combine(_root, "blue.jpg");
        using (var red = new RasterImage(8, 8, new RgbPixel(255, 0, 0))) await red.SaveAsJpegAsync(redPath, 90);
        using (var blue = new RasterImage(8, 8, new RgbPixel(0, 0, 255))) await blue.SaveAsJpegAsync(bluePath, 90);
        var handler = new FramesHandler(redPath, bluePath);
        using var client = new HttpClient(handler);
        using var service = new ServiceVideoMediaProcessor(new Uri("http://localhost/"), "", Path.Combine(_root, "cache"), client);
        var original = Record(knownHash ? new string('a', 64) : null);
        var first = await service.GetOrCreatePosterAsync(original);
        var firstBytes = await File.ReadAllBytesAsync(first);
        var second = await service.GetOrCreatePosterAsync(original with { Sha256 = knownHash ? new string('b', 64) : null });
        Assert.Equal(2, handler.Calls);
        Assert.NotEqual(first, second);
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(first));
        Assert.NotEqual(Convert.ToHexString(firstBytes), Convert.ToHexString(await File.ReadAllBytesAsync(second)));
        if (knownHash)
        {
            Assert.Equal(second, await service.GetOrCreatePosterAsync(original with { Sha256 = new string('B', 64) }));
            Assert.Equal(2, handler.Calls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalPosterUsesActualContentHashInsteadOfTimestampOrStaleSignature(bool staleSignature)
    {
        var video = Record(staleSignature ? new string('a', 64) : null);
        var cache = Path.Combine(_root, "cache");
        await File.WriteAllBytesAsync(video.Path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(video.Path, video.ModifiedUtc);
        var firstDirectory = await VideoMediaCachePaths.DirectoryAsync(cache, video, true, default);
        Directory.CreateDirectory(firstDirectory);
        var firstPoster = Path.Combine(firstDirectory, "poster.jpg"); await File.WriteAllBytesAsync(firstPoster, [11]);
        var processor = new FfmpegKeyframeService(Path.Combine(_root, "missing-ffmpeg.exe"), cache);
        Assert.Equal(firstPoster, await processor.GetOrCreatePosterAsync(video));
        await File.WriteAllBytesAsync(video.Path, [4, 5, 6]);
        File.SetLastWriteTimeUtc(video.Path, video.ModifiedUtc);
        var secondDirectory = await VideoMediaCachePaths.DirectoryAsync(cache, video, true, default);
        Assert.NotEqual(firstDirectory, secondDirectory);
        Directory.CreateDirectory(secondDirectory);
        var secondPoster = Path.Combine(secondDirectory, "poster.jpg"); await File.WriteAllBytesAsync(secondPoster, [22]);
        Assert.Equal(secondPoster, await processor.GetOrCreatePosterAsync(video));
        Assert.Equal(new byte[] { 11 }, await File.ReadAllBytesAsync(firstPoster));
    }

    [Fact]
    public async Task FailedLocalExtractionRemovesOnlyItsStagingAndRunDirectory()
    {
        var cache = Path.Combine(_root, "cache");
        var existing = Path.Combine(cache, "7", new string('a', 64), "run-previous");
        Directory.CreateDirectory(existing); var frame = Path.Combine(existing, "scene-00001.jpg");
        await File.WriteAllBytesAsync(frame, [42]);
        var video = Record(new string('b', 64));
        await File.WriteAllBytesAsync(video.Path, [1, 2, 3]);
        var newContentDirectory = await VideoMediaCachePaths.DirectoryAsync(cache, video, true, default);
        var processor = new FfmpegKeyframeService(Path.Combine(_root, "missing-ffmpeg.exe"), cache);
        await Assert.ThrowsAnyAsync<Exception>(() => processor.ExtractAsync(video));
        Assert.Empty(Directory.GetDirectories(cache, ".7-keyframes-*"));
        Assert.Empty(Directory.GetDirectories(newContentDirectory, "run-*"));
        Assert.Equal(new byte[] { 42 }, await File.ReadAllBytesAsync(frame));
    }

    private sealed class FramesHandler(string first, string second) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = ++Calls == 1 ? first : second;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new[] { new VideoKeyframe(0, path) }) });
        }
    }
}
