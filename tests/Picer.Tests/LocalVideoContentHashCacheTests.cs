using System.Security.Cryptography;
using Picer.Core.Video;

namespace Picer.Tests;

public sealed class LocalVideoContentHashCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-local-hash-" + Guid.NewGuid().ToString("N"));
    public LocalVideoContentHashCacheTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task UnchangedNtfsFileAvoidsSecondFullReadButSameMtimeReplacementDoesNot()
    {
        var path = Path.Combine(_root, "video.mp4"); await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var modified = File.GetLastWriteTimeUtc(path);
        var reads = 0;
        var cache = new LocalVideoContentHashCache(async (stream, token) =>
        { reads++; return await SHA256.HashDataAsync(stream, token); });
        var first = await cache.GetHashAsync(path, default);
        Assert.Equal(first, await cache.GetHashAsync(path, default));
        if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(path)!).DriveFormat == "NTFS")
            Assert.Equal(1, reads);
        else Assert.Equal(2, reads);
        var beforeReplacement = reads;
        await File.WriteAllBytesAsync(path, [4, 5, 6]); File.SetLastWriteTimeUtc(path, modified);
        Assert.NotEqual(first, await cache.GetHashAsync(path, default));
        Assert.Equal(beforeReplacement + 1, reads);
    }

    [Fact]
    public async Task CacheIsBoundedAndExplicitPurgeForcesFreshRead()
    {
        var reads = 0;
        var cache = new LocalVideoContentHashCache(async (stream, token) =>
        { reads++; return await SHA256.HashDataAsync(stream, token); }, capacity: 2);
        string path = "";
        for (var index = 0; index < 5; index++)
        {
            path = Path.Combine(_root, index + ".mp4"); await File.WriteAllBytesAsync(path, [(byte)index]);
            await cache.GetHashAsync(path, default);
            Assert.InRange(cache.Count, 0, 2);
        }
        cache.Invalidate(path);
        var before = reads;
        await cache.GetHashAsync(path, default);
        Assert.Equal(before + 1, reads);
    }

    [Fact]
    public async Task PurgeWhileHashingPreventsLateCachePublication()
    {
        var path = Path.Combine(_root, "video.mp4"); await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new LocalVideoContentHashCache(async (stream, token) =>
        {
            started.TrySetResult(); await release.Task.WaitAsync(token);
            return await SHA256.HashDataAsync(stream, token);
        });
        var pending = cache.GetHashAsync(path, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cache.Invalidate(path); release.TrySetResult();
        await pending;
        Assert.Equal(0, cache.Count);
    }
}
