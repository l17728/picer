using Picer.Core.Indexing;

namespace Picer.Tests;

public sealed class DirectoryMediaChangeTrackerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-directory-tracker-" + Guid.NewGuid().ToString("N"));
    public DirectoryMediaChangeTrackerTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private static bool Photo(string path) => path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task UnchangedRunReusesListingsWhileForcedAndInvalidatedRunsEnumerate()
    {
        var child = Directory.CreateDirectory(Path.Combine(_root, "child")).FullName;
        File.WriteAllText(Path.Combine(child, "one.jpg"), "one");
        using var tracker = new DirectoryMediaChangeTracker();
        var first = tracker.Read(_root, false, Photo, default);
        Assert.Equal(2, first.EnumeratedDirectories);
        var second = tracker.Read(_root, false, Photo, default);
        // Directory timestamp updates and native watcher notifications can arrive after
        // File.WriteAllText returns, especially alongside the full parallel test suite.
        // A scan overlapping those notifications must conservatively rebuild; require
        // eventual reuse after quiescence rather than suppressing that safety fallback.
        var settling = System.Diagnostics.Stopwatch.StartNew();
        while ((!second.Complete || second.EnumeratedDirectories != 0) && settling.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(20);
            second = tracker.Read(_root, false, Photo, default);
        }
        Assert.True(second.Complete && second.EnumeratedDirectories == 0,
            $"Cache did not settle: firstComplete={first.Complete}; firstFailures={first.Failures.Length}; " +
            $"lastComplete={second.Complete}; lastFailures={second.Failures.Length}; enumerated={second.EnumeratedDirectories}");
        Assert.Equal(2, second.ReusedDirectories);
        Assert.Equal(first.Files, second.Files);
        Assert.Equal(2, tracker.Read(_root, true, Photo, default).EnumeratedDirectories);
        tracker.Invalidate(_root);
        Assert.Equal(2, tracker.Read(_root, false, Photo, default).EnumeratedDirectories);
    }

    [Fact]
    public async Task ChangesIncludeAddedRenamedAndRemovedSubtrees()
    {
        var oldDirectory = Directory.CreateDirectory(Path.Combine(_root, "old")).FullName;
        File.WriteAllText(Path.Combine(oldDirectory, "one.jpg"), "one");
        using var tracker = new DirectoryMediaChangeTracker();
        tracker.Read(_root, false, Photo, default);
        var renamed = Path.Combine(_root, "renamed");
        Directory.Move(oldDirectory, renamed);
        var added = Directory.CreateDirectory(Path.Combine(_root, "added")).FullName;
        File.WriteAllText(Path.Combine(added, "two.jpg"), "two");
        // Native watcher delivery and directory timestamp updates are asynchronous.
        // Require a complete, exact result after bounded settling, not an immediate
        // snapshot that production intentionally treats as incomplete during changes.
        var expected = new[] { Path.Combine(renamed, "one.jpg"), Path.Combine(added, "two.jpg") }
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var changed = tracker.Read(_root, false, Photo, default);
        var settling = System.Diagnostics.Stopwatch.StartNew();
        while ((!changed.Complete || !changed.Files.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
            && settling.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(20);
            changed = tracker.Read(_root, false, Photo, default);
        }
        Assert.True(changed.Complete, "Added/renamed subtree notifications did not settle into a complete scan.");
        Assert.Equal(2, changed.Files.Length);
        Assert.Equal(expected, changed.Files);
        Assert.Contains(Path.Combine(added, "two.jpg"), changed.Files);
        Assert.Contains(Path.Combine(renamed, "one.jpg"), changed.Files);
        Assert.DoesNotContain(changed.Files, path => path.StartsWith(oldDirectory, StringComparison.OrdinalIgnoreCase));
        Directory.Delete(renamed, true);
        var deleted = tracker.Read(_root, false, Photo, default);
        settling.Restart();
        var remaining = new[] { Path.Combine(added, "two.jpg") };
        while ((!deleted.Complete || !deleted.Files.SequenceEqual(remaining, StringComparer.OrdinalIgnoreCase))
            && settling.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(20);
            deleted = tracker.Read(_root, false, Photo, default);
        }
        Assert.True(deleted.Complete, "Deleted subtree notifications did not settle into a complete scan.");
        Assert.Single(deleted.Files);
        Assert.Equal(remaining, deleted.Files);
        Assert.DoesNotContain(deleted.Files, path => path.StartsWith(renamed, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CanceledWalkDoesNotPublishPartialDirectoryCache()
    {
        File.WriteAllText(Path.Combine(_root, "one.jpg"), "one");
        File.WriteAllText(Path.Combine(_root, "two.jpg"), "two");
        using var tracker = new DirectoryMediaChangeTracker();
        using var cancel = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => tracker.Read(_root, false, path => { cancel.Cancel(); return Photo(path); }, cancel.Token));
        var complete = tracker.Read(_root, false, Photo, default);
        Assert.Equal(2, complete.Files.Length);
        Assert.Equal(1, complete.EnumeratedDirectories);
        Assert.True(complete.Complete);
    }

    [Fact]
    public void MonitoringGapDuringEnumerationCannotAuthorizeMissingReconciliation()
    {
        File.WriteAllText(Path.Combine(_root, "one.jpg"), "one");
        using var tracker = new DirectoryMediaChangeTracker();
        var interrupted = tracker.Read(_root, false, path => { tracker.Invalidate(_root); return Photo(path); }, default);
        Assert.Single(interrupted.Files);
        Assert.False(interrupted.Complete);
        var recovered = tracker.Read(_root, false, Photo, default);
        Assert.Equal(1, recovered.EnumeratedDirectories);
        Assert.True(recovered.Complete);
    }

    [Fact]
    public async Task ExpiredCacheAndNewTrackerPerformFullEnumeration()
    {
        File.WriteAllText(Path.Combine(_root, "one.jpg"), "one");
        using var tracker = new DirectoryMediaChangeTracker(TimeSpan.FromMilliseconds(30));
        tracker.Read(_root, false, Photo, default);
        await Task.Delay(60);
        Assert.Equal(1, tracker.Read(_root, false, Photo, default).EnumeratedDirectories);
        using var restarted = new DirectoryMediaChangeTracker();
        Assert.Equal(1, restarted.Read(_root, false, Photo, default).EnumeratedDirectories);
    }

    [Fact]
    public async Task LegacyVideoWithoutHashIsReadOnceAndThenReused()
    {
        var path = Path.Combine(_root, "video.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        using var tracker = new DirectoryMediaChangeTracker();
        var calls = 0;
        var scanner = new DirectoryMediaScanner((_, _, _) => { calls++; return Task.FromResult(new DirectoryMediaMetadata(32, 32)); }, tracker);
        var old = new DirectoryMediaEntry(path, 3, File.GetLastWriteTimeUtc(path), true, null, new(32, 32));
        var first = await scanner.ReadAsync(new(1, _root, KnownEntries: [old]));
        Assert.Equal(64, Assert.Single(first.Items).Sha256!.Length);
        Assert.False(first.Items[0].Reused);
        var second = await scanner.ReadAsync(new(1, _root, KnownEntries: first.Items));
        Assert.True(Assert.Single(second.Items).Reused);
        Assert.Equal(1, calls);
    }
}
