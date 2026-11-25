using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Picer.App.Services;
using Picer.Core.Gateway;

namespace Picer.E2E;

public sealed class SharedImageAssetsTests
{
    [Fact]
    public async Task VideoFaceThumbnailDoesNotRetainAnUnboundedFullFrame()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-face-crop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "frame.png");
            var source = System.Windows.Media.Imaging.BitmapSource.Create(1000, 1000, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, new byte[4000000], 4000);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
            using (var output = File.Create(path)) encoder.Save(output);
            var face = new Picer.Core.Video.VideoFaceTimelineRecord(0, path, new(.1f, .1f, .7f, .7f), 1, 1, null, null, null, null, null, null);
            var item = new Picer.App.ViewModels.VideoFaceItemViewModel(face);
            await item.LoadThumbnailAsync();
            var thumbnail = Assert.IsType<System.Windows.Media.Imaging.WriteableBitmap>(item.Thumbnail);
            Assert.True(thumbnail.IsFrozen);
            Assert.InRange(thumbnail.PixelWidth, 1, 256); Assert.InRange(thumbnail.PixelHeight, 1, 256);
            File.Delete(path);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RemoteVideoFaceCropUsesAlreadyMaterializedServerFrameWithoutMirrorLookup()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-remote-face-crop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var serverFrame = Path.Combine(root, "server-frame.png");
            var source = System.Windows.Media.Imaging.BitmapSource.Create(64, 64, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, new byte[64 * 64 * 4], 64 * 4);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
            using (var output = File.Create(serverFrame)) encoder.Save(output);
            var services = new Picer.App.ClientServices
            {
                ServiceMode = true,
                RemoteThumbnails = new RemoteThumbnailCache(root)
            };
            var face = new Picer.Core.Video.VideoFaceTimelineRecord(0, "server-path-not-on-this-client.jpg",
                new(.1f, .1f, .7f, .7f), 1, 12, null, null, null, null, null, null);
            var item = new Picer.App.ViewModels.VideoFaceItemViewModel(face, services, serverFrame);

            await item.LoadThumbnailAsync();

            Assert.True(item.HasThumbnail);
            Assert.False(item.IsThumbnailLoading);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task VideoDetailsAndPeopleReuseTheSamePersistentFrame()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-shared-frame-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1sAAAAASUVORK5CYII=");
            var handler = new Handler(bytes);
            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://fixture.invalid/") };
            using var gateway = new HttpIndexGateway(client);
            var services = new Picer.App.ClientServices { Gateway = gateway, RemoteThumbnails = new(root), RemoteVideoCacheRoot = root };
            var video = new Picer.Core.Video.VideoRecord(7, 1, "remote.mp4", 10, DateTime.UtcNow, 1, 10, 10, null, null, "active") { Sha256 = new string('a', 64) };
            var frame = new Picer.Core.Video.VideoKeyframe(0, "remote-frame.jpg") { Id = 11 };
            var one = await RemoteKeyframeAssets.GetAsync(services, video, frame, 0, default, prefetch: true);
            var two = await RemoteKeyframeAssets.GetAsync(services, video, frame, 0, default);
            Assert.NotNull(one); Assert.Equal(one, two); Assert.Equal(1, handler.Calls);
            var secondReference = await RemoteKeyframeAssets.GetAsync(services, video with { Id = 8 }, frame, 0, default);
            Assert.Equal(one, secondReference); Assert.Equal(1, handler.Calls);
            Assert.NotEqual(one, await RemoteKeyframeAssets.GetAsync(services, video with { Sha256 = new string('b', 64) }, frame, 0, default));
            Assert.Equal(2, handler.Calls);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OriginalViewersShareDownloadsButSpacesAndPreviewVariantsStaySeparate()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-shared-original-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var data = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1sAAAAASUVORK5CYII=");
            var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            var handler = new Handler(data);
            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://fixture.invalid/") };
            using var gateway = new HttpIndexGateway(client);
            var cache = new PlayableMediaCache(root);
            Task<string> Open(string space, long id, bool preview = false) => SharedImageAssets.GetCachedAsync(
                cache, space, hash, gateway, id, "photo.jpg", hash, preview, default);
            var together = await Task.WhenAll(Open("space-a", 1), Open("space-a", 2));
            Assert.Equal(together[0], together[1]); Assert.Equal(1, handler.Calls);
            Assert.Equal(together[0], await Open("space-a", 1)); Assert.Equal(1, handler.Calls);
            Assert.NotEqual(together[0], await Open("space-b", 1));
            Assert.NotEqual(together[0], await Open("space-a", 1, true));
            Assert.Equal(3, handler.Calls);
            Assert.Equal(data, await File.ReadAllBytesAsync(together[0]));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Handler(byte[] data) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
        }
    }
}
