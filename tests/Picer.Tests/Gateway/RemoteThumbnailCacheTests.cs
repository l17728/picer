using System.Text;
using Picer.Core.Gateway;
using Picer.Core.Models;
using Picer.Core.Security;

namespace Picer.Tests.Gateway;

public sealed class RemoteThumbnailCacheTests
{
    [Fact]
    public async Task EmptyResponseIsNotPublishedAndNextRequestCanRecover()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-thumb-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cache = new RemoteThumbnailCache(root);
            var record = new ImageRecord { Id = 55 };
            await Assert.ThrowsAsync<IOException>(() => cache.GetOrDownloadAsync(record,
                () => Task.FromResult<Stream?>(new MemoryStream())));
            Assert.Null(cache.TryGet(record));
            Assert.Empty(Directory.EnumerateFiles(root));
            var path = await cache.GetOrDownloadAsync(record,
                () => Task.FromResult<Stream?>(new MemoryStream([1, 2, 3])));
            Assert.NotNull(path);
            Assert.Equal(path, new RemoteThumbnailCache(root).TryGet(record));
            cache.Invalidate(record);
            Assert.Null(cache.TryGet(record));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ViewportBypassesQueuedPrefetchAndPromotionUsesReservedCapacity()
    {
        var scheduler = new ThumbnailWorkScheduler(2, 1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var background = await scheduler.EnterAsync(() => false, deadline.Token);
        var promoted = false;
        var waiting = scheduler.EnterAsync(() => Volatile.Read(ref promoted), deadline.Token);
        Assert.False(waiting.IsCompleted);
        using (await scheduler.EnterAsync(() => true, deadline.Token).WaitAsync(TimeSpan.FromSeconds(2)))
            Assert.False(waiting.IsCompleted);
        Volatile.Write(ref promoted, true);
        scheduler.RefreshPriorities();
        using var visible = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CancellingOneSubscriberKeepsSharedDownloadUntilLastSubscriberLeaves()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-thumb-shared-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cache = new RemoteThumbnailCache(root);
            var record = new ImageRecord { Id = 7, ContentSha256 = new string('a', 64) };
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var active = 0; var calls = 0;
            async Task<Stream?> Open(CancellationToken token)
            {
                Interlocked.Increment(ref calls); Interlocked.Increment(ref active); entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); return null; }
                finally { Interlocked.Decrement(ref active); }
            }
            using var firstStop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var secondStop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var first = cache.GetOrDownloadAsync(record, Open, firstStop.Token, prefetch: true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = cache.GetOrDownloadAsync(record, Open, secondStop.Token);
            firstStop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.Equal(1, active); Assert.Equal(1, calls); Assert.False(second.IsCompleted);
            secondStop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            Assert.Equal(0, active); Assert.Empty(Directory.EnumerateFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task QueuedOrphanIsRemovedAndNewViewportRequestCanStartImmediately()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-thumb-priority-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<string?>? busy = null;
        try
        {
            var cache = new RemoteThumbnailCache(root);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            busy = cache.GetOrDownloadAsync(new ImageRecord { Id = 1 }, async token =>
            { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return null; }, deadline.Token, prefetch: true);
            await entered.Task.WaitAsync(deadline.Token);
            var next = new ImageRecord { Id = 2 };
            var calls = 0;
            Task<Stream?> Open(CancellationToken _) { calls++; return Task.FromResult<Stream?>(new MemoryStream([1, 2, 3])); }
            using var orphan = new CancellationTokenSource();
            var waiting = cache.GetOrDownloadAsync(next, Open, orphan.Token, prefetch: true);
            Assert.Equal(0, calls);
            orphan.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Equal(0, calls);
            var visible = await cache.GetOrDownloadAsync(next, Open, deadline.Token).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(File.Exists(visible)); Assert.Equal(1, calls); Assert.False(busy.IsCompleted);
        }
        finally
        {
            deadline.Cancel();
            if (busy is not null) { try { await busy; } catch (OperationCanceledException) { } }
            Directory.Delete(root, true);
        }
    }

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

    [Fact]
    public async Task EncryptedCacheStoresCiphertextAndServesPlaintextOnlyFromReadStream()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-thumb-encrypted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var record = new ImageRecord { Id = 12, ContentSha256 = new string('b', 64) };
        try
        {
            string path;
            using (var cache = new RemoteThumbnailCache(root, new MediaFileCipher(key)))
            {
                path = Assert.IsType<string>(await cache.GetOrDownloadAsync(record,
                    () => Task.FromResult<Stream?>(new MemoryStream("private-thumbnail"u8.ToArray()))));
                var stored = await File.ReadAllBytesAsync(path);
                Assert.NotEqual("private-thumbnail"u8.ToArray(), stored);
                using var decoded = new StreamReader(cache.OpenRead(record));
                Assert.Equal("private-thumbnail", await decoded.ReadToEndAsync());
            }

            using (var reopened = new RemoteThumbnailCache(root, new MediaFileCipher(key)))
            {
                var migrated = reopened.ProtectExistingCacheFiles();
                Assert.Equal((0, 0), migrated);
                Assert.Equal(path, reopened.TryGet(record));
                using var decoded = new StreamReader(reopened.OpenReadPath(path));
                Assert.Equal("private-thumbnail", await decoded.ReadToEndAsync());
            }
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ExistingPlaintextCacheIsMigratedInPlace()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-thumb-migrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var record = new ImageRecord { Id = 13, ContentSha256 = new string('c', 64) };
        try
        {
            string path;
            using (var plain = new RemoteThumbnailCache(root))
                path = Path.Combine(root, plain.GetKey(record) + ".jpg");
            File.WriteAllBytes(path, "old-thumbnail"u8.ToArray());

            using var encrypted = new RemoteThumbnailCache(root, new MediaFileCipher(key));
            Assert.Equal((1, 0), encrypted.ProtectExistingCacheFiles());
            Assert.NotEqual("old-thumbnail"u8.ToArray(), File.ReadAllBytes(path));
            using var source = new StreamReader(encrypted.OpenRead(record));
            Assert.Equal("old-thumbnail", source.ReadToEnd());
            Assert.Equal((0, 0), encrypted.ProtectExistingCacheFiles());
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
            Directory.Delete(root, true);
        }
    }
}
