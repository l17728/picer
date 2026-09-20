using System.Security.Cryptography;
using Picer.Core.Gateway;

namespace Picer.Tests;

public sealed class RemoteAssetCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-asset-cache-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DownloadsOnceAndReusesContentAddressedFile()
    {
        Directory.CreateDirectory(_root);
        var bytes = Enumerable.Range(0, 4096).Select(value => (byte)(value % 251)).ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var opens = 0;
        var cache = new RemoteAssetCache(_root, 1024 * 1024);
        var first = await cache.GetOrDownloadAsync(hash, () => { opens++; return new MemoryStream(bytes, writable: false); });
        var second = await cache.GetOrDownloadAsync(hash, () => { opens++; return new MemoryStream(bytes, writable: false); });
        Assert.Equal(first, second);
        Assert.Equal(1, opens);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(first));
    }

    [Fact]
    public async Task RejectsHashMismatchAndDoesNotLeavePartialFile()
    {
        Directory.CreateDirectory(_root);
        var expected = new string('a', 64);
        var cache = new RemoteAssetCache(_root);
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.GetOrDownloadAsync(expected, () => new MemoryStream([1, 2, 3])));
        Assert.Empty(Directory.EnumerateFiles(_root));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
