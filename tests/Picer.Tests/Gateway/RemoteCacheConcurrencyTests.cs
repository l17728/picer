using System.Security.Cryptography;
using Picer.Core.Gateway;
using Picer.Core.Models;

namespace Picer.Tests.Gateway;

public sealed class RemoteCacheConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-cache-concurrency-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    [Fact]
    public async Task CancelingFirstThumbnailSubscriberDoesNotCancelSharedDownload()
    {
        var cache = new RemoteThumbnailCache(_root);
        var image = new ImageRecord { Id = 1, Sha256 = new string('a', 64) };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<Stream?> Open(CancellationToken token)
        {
            Interlocked.Increment(ref calls); started.SetResult();
            await finish.Task.WaitAsync(token);
            return new MemoryStream([1, 2, 3]);
        }
        using var cancel = new CancellationTokenSource();
        var first = cache.GetOrDownloadAsync(image, Open, cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = cache.GetOrDownloadAsync(image, Open);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        finish.SetResult();
        Assert.True(File.Exists(await second.WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FailedThumbnailGenerationCanBeRetriedAndShared()
    {
        var cache = new RemoteThumbnailCache(_root);
        var image = new ImageRecord { Id = 1, Sha256 = new string('a', 64) };
        await Assert.ThrowsAsync<IOException>(() => cache.GetOrDownloadAsync(image, _ => Task.FromException<Stream?>(new IOException("test"))));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<Stream?> Open(CancellationToken token) { Interlocked.Increment(ref calls); await release.Task.WaitAsync(token); return new MemoryStream([1]); }
        var tasks = Enumerable.Range(0, 4).Select(_ => cache.GetOrDownloadAsync(image, Open)).ToArray();
        release.SetResult();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DifferentAssetsRunConcurrentlyButIdenticalContentOpensOnce()
    {
        var cache = new RemoteAssetCache(_root, 1024, maximumConcurrentDownloads: 2);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        byte[] slowBytes = [1, 2, 3];
        Stream Slow() { Interlocked.Increment(ref calls); return new GatedStream(slowBytes, started, release); }
        var slow = cache.GetOrDownloadAsync(Hash(slowBytes), Slow);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicate = cache.GetOrDownloadAsync(Hash(slowBytes), Slow);
        byte[] fastBytes = [4, 5];
        var fast = await cache.GetOrDownloadAsync(Hash(fastBytes), () => new MemoryStream(fastBytes)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(fast)); Assert.False(slow.IsCompleted);
        release.SetResult();
        Assert.Equal(await slow, await duplicate); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancellationRemovesPartialsAndOversizedAssetCannotExceedQuota()
    {
        var cache = new RemoteAssetCache(_root, 4, maximumConcurrentDownloads: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var pending = cache.GetOrDownloadAsync(Hash([1, 2]), () => new GatedStream([1, 2], started, release), cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(Directory.EnumerateFiles(_root));
        await Assert.ThrowsAsync<IOException>(() => cache.GetOrDownloadAsync(Hash([1, 2, 3, 4, 5]), () => new MemoryStream([1, 2, 3, 4, 5])));
        Assert.Empty(Directory.EnumerateFiles(_root));
        await cache.GetOrDownloadAsync(Hash([1, 2, 3]), () => new MemoryStream([1, 2, 3]));
        await cache.GetOrDownloadAsync(Hash([4, 5, 6]), () => new MemoryStream([4, 5, 6]));
        Assert.True(Directory.EnumerateFiles(_root).Sum(path => new FileInfo(path).Length) <= 4);
    }

    [Fact]
    public async Task ClearPreventsInFlightDownloadFromRestoringDeletedCache()
    {
        var cache = new RemoteAssetCache(_root, 1024);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = cache.GetOrDownloadAsync(Hash([1, 2]), () => new GatedStream([1, 2], started, release));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cache.Clear(); release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(Directory.EnumerateFiles(_root));
    }

    private sealed class GatedStream(byte[] bytes, TaskCompletionSource started, TaskCompletionSource release) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
