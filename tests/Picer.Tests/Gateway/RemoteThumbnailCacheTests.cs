using System.Text;
using Picer.Core.Gateway;
using Picer.Core.Models;

namespace Picer.Tests.Gateway;

public sealed class RemoteThumbnailCacheTests
{
    [Fact]
    public async Task SameContentSharesOneDownloadAndPersists()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-thumb-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cache = new RemoteThumbnailCache(root);
            var record = new ImageRecord { Id = 7, ContentSha256 = new string('a', 64) };
            var calls = 0;
            async Task<Stream?> Open()
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(20);
                return new MemoryStream(Encoding.UTF8.GetBytes("thumbnail"));
            }

            var paths = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => cache.GetOrDownloadAsync(record, Open)));

            Assert.All(paths, path => Assert.Equal(paths[0], path));
            Assert.Equal(1, calls);
            Assert.True(File.Exists(paths[0]));

            var second = new RemoteThumbnailCache(root);
            var persisted = await second.GetOrDownloadAsync(record, () =>
                throw new InvalidOperationException("cache should be reused"));
            Assert.Equal(paths[0], persisted);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
