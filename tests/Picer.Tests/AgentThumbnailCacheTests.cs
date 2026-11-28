using System.Security.Cryptography;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Imaging;
using Picer.Core.Models;
using Picer.Core.Security;
using Picer.Core.Thumbnails;
using Picer.IndexService;

namespace Picer.Tests;

public sealed class AgentThumbnailCacheTests
{
    [Fact]
    public async Task HttpsReturnsAgentThumbnailAfterServiceRestartWithoutAgent()
    {
        var root = Directory.CreateTempSubdirectory("picer-offline-thumb-").FullName;
        var index = Path.Combine(root, "index");
        Directory.CreateDirectory(index);
        try
        {
            using (var image = new RasterImage(512, 256, new RgbPixel(70, 30, 50)))
                image.SaveAsJpeg(Path.Combine(root, "source.jpg"));
            long id;
            using (var db = new PicerDatabase(Path.Combine(index, "picer.db")))
            {
                var source = db.AddSource(root);
                id = db.UpsertImage(new ImageRecord { SourceId = source.Id, Path = Path.Combine(root, "remote", "source.jpg"),
                    IndexedUtc = DateTime.UtcNow, ModifiedUtc = DateTime.UtcNow, Status = ImageStatus.Active }).Id;
            }
            var first = await ReadAsync(true);
            using (var image = RasterImage.Load(new MemoryStream(first)))
                Assert.Equal(256, image.Width);
            Assert.Equal(first, await ReadAsync(false));

            async Task<byte[]> ReadAsync(bool online)
            {
                var service = IndexServiceHost.Build(index, port: 0, bearerToken: "picer-test-token");
                try
                {
                    var url = await IndexServiceHost.StartAsync(service.App);
                    var pin = ServiceSecurity.ComputeSpkiPin(service.Certificate);
                    await using var agent = online ? await MediaStreamingTests.TestAgent.StartAsync(root, url, pin) : null;
                    using var gateway = new HttpIndexGateway(url, "picer-test-token", pin);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    using var stream = await gateway.OpenThumbnailAsync(id, timeout.Token);
                    Assert.NotNull(stream);
                    using var copy = new MemoryStream();
                    await stream.CopyToAsync(copy, timeout.Token);
                    if (agent is not null) Assert.Equal(1, agent.PreviewRequests);
                    return copy.ToArray();
                }
                finally
                {
                    await service.App.StopAsync(); await service.App.DisposeAsync();
                    service.Certificate.Dispose(); service.Handlers.DisposeDatabase();
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RestartReadsEncryptedThumbnailOfflineAndVersionChangeFetchesAgain()
    {
        var directory = Directory.CreateTempSubdirectory("picer-agent-thumb-");
        try
        {
            var key = RandomNumberGenerator.GetBytes(32);
            using var image = new RasterImage(32, 16, new RgbPixel(30, 50, 70));
            using var jpeg = new MemoryStream();
            image.SaveAsJpeg(jpeg);
            var bytes = jpeg.ToArray();
            var calls = 0;
            Task<byte[]> Fetch(CancellationToken token) { calls++; return Task.FromResult(bytes); }
            using (var first = new ThumbnailService(directory.FullName, cipher: new MediaFileCipher(key)))
                Assert.Equal(bytes, await first.GetAgentThumbnailAsync(17, "revision-one", Fetch, default));
            var stored = Assert.Single(directory.GetFiles());
            Assert.False(File.ReadAllBytes(stored.FullName).SequenceEqual(bytes));
            using var restarted = new ThumbnailService(directory.FullName, cipher: new MediaFileCipher(key));
            Assert.Equal(bytes, await restarted.GetAgentThumbnailAsync(17, "revision-one", _ => throw new IOException("offline"), default));
            Assert.Equal(bytes, await restarted.GetAgentThumbnailAsync(17, "revision-two", Fetch, default));
            Assert.Equal(2, calls);
            restarted.Delete(17);
            Assert.Empty(directory.GetFiles());
        }
        finally { directory.Delete(true); }
    }
}
