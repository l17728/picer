using System.Security.Cryptography;
using System.Buffers.Binary;
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
        bool trustedIdentity;
        using (var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            trustedIdentity = LocalVideoContentHashCache.ReadSignature(path, probe) is not null;
        var first = await cache.GetHashAsync(path, default);
        Assert.Equal(first, await cache.GetHashAsync(path, default));
        if (trustedIdentity)
            Assert.Equal(1, reads);
        else
        {
            Assert.Equal(2, reads);
            Assert.Equal(0, cache.Count);
        }
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
    public async Task RapidSameLengthRewritesWithRestoredMtimeAlwaysHashCurrentBytes()
    {
        var path = Path.Combine(_root, "rapid-rewrite.mp4");
        var timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var cache = new LocalVideoContentHashCache();
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var bytes = new byte[] { (byte)iteration, 42, 7, 99 };
            await File.WriteAllBytesAsync(path, bytes);
            File.SetLastWriteTimeUtc(path, timestamp);
            var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Assert.Equal(expected, await cache.GetHashAsync(path, default));
        }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileUsnOrJournalGenerationInvalidatesHashWithIdenticalFileIdentityAndTimestamps(bool recreateJournal)
    {
        var path = Path.Combine(_root, "same-signature.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var modified = File.GetLastWriteTimeUtc(path);
        long fileUsn = 100; ulong journalId = 7; var reads = 0;
        var cache = new LocalVideoContentHashCache(async (stream, token) =>
        { reads++; return await SHA256.HashDataAsync(stream, token); },
            signature: (_, stream) => new(1, 2, 0, 12345, stream.Length, journalId, fileUsn));
        var first = await cache.GetHashAsync(path, default);
        Assert.Equal(first, await cache.GetHashAsync(path, default));
        Assert.Equal(1, reads);
        await File.WriteAllBytesAsync(path, [4, 5, 6]); File.SetLastWriteTimeUtc(path, modified);
        if (recreateJournal) journalId++; else fileUsn++;
        Assert.NotEqual(first, await cache.GetHashAsync(path, default));
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task MissingTrustedJournalAlwaysRehashesAndDoesNotRetainSignature()
    {
        var path = Path.Combine(_root, "no-journal.mp4"); await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var reads = 0;
        var cache = new LocalVideoContentHashCache(async (stream, token) =>
        { reads++; return await SHA256.HashDataAsync(stream, token); }, signature: (_, _) => null);
        var first = await cache.GetHashAsync(path, default);
        Assert.Equal(first, await cache.GetHashAsync(path, default));
        Assert.Equal(2, reads);
        await File.WriteAllBytesAsync(path, [4, 5, 6]);
        Assert.NotEqual(first, await cache.GetHashAsync(path, default));
        Assert.Equal(3, reads);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void FileUsnRecordMustMatchHandleIdentityAndSupportedFormat()
    {
        var record = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(record, 64);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), 42);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(24), 100);
        Assert.Equal(100L, LocalVideoContentHashCache.ParseFileUsn(record, 42, 0));
        Assert.Null(LocalVideoContentHashCache.ParseFileUsn(record, 43, 0));
        Assert.Null(LocalVideoContentHashCache.ParseFileUsn(record, 42, 1));
        Assert.Null(LocalVideoContentHashCache.ParseFileUsn(record.AsSpan(0, 40), 42, 0));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 3);
        Assert.Null(LocalVideoContentHashCache.ParseFileUsn(record, 42, 0));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 2);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(24), 0);
        Assert.Null(LocalVideoContentHashCache.ParseFileUsn(record, 42, 0));
    }
}
