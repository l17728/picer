using System.Buffers.Binary;
using System.Text;
using Picer.Core.Indexing;

namespace Picer.Tests;

public sealed class PersistentDirectoryScanTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "picer-usn-scan-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_workspace, "photos");
    private string Cache => Path.Combine(_workspace, "manifests");
    public PersistentDirectoryScanTests() => Directory.CreateDirectory(Root);
    public void Dispose() { if (Directory.Exists(_workspace)) Directory.Delete(_workspace, true); }
    private static bool Photo(string path) => path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase);
    private DirectoryMediaChangeTracker Tracker(FakeJournal journal) => new(persistentCacheRoot: Cache, journal: journal);

    private async Task<DirectoryMediaChangeTracker.Listing> CompleteBaseline(DirectoryMediaChangeTracker tracker)
    {
        // Delayed fixture-creation events must settle before a restart test may
        // assume that production code published a trusted persistent baseline.
        var listing = tracker.Read(Root, false, Photo, default);
        var settling = System.Diagnostics.Stopwatch.StartNew();
        while (!listing.Complete && settling.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(25);
            listing = tracker.Read(Root, false, Photo, default);
        }
        Assert.True(listing.Complete, "Initial directory creation did not settle into a complete baseline.");
        Assert.NotNull(new DirectoryScanManifestStore(Cache).Load(Root));
        return listing;
    }

    private async Task<DirectoryMediaPage> CompletePage(Func<Task<DirectoryMediaPage>> read)
    {
        var page = await read();
        var settling = System.Diagnostics.Stopwatch.StartNew();
        while (!page.EnumerationComplete && settling.Elapsed < TimeSpan.FromSeconds(3))
        {
            await Task.Delay(25);
            page = await read();
        }
        Assert.True(page.EnumerationComplete, "Directory notifications did not settle into a complete media page.");
        Assert.NotNull(new DirectoryScanManifestStore(Cache).Load(Root));
        return page;
    }

    private async Task<DirectoryMediaChangeTracker.Listing> CompleteRestartedListing(FakeJournal journal)
    {
        var manifestPath = Assert.Single(Directory.GetFiles(Cache, "*.scan"));
        var baseline = await File.ReadAllBytesAsync(manifestPath);
        var settling = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            DirectoryMediaChangeTracker.Listing listing;
            using (var restarted = Tracker(journal)) listing = restarted.Read(Root, false, Photo, default);
            if (listing.Complete || settling.Elapsed >= TimeSpan.FromSeconds(3)) return listing;
            // A notification during the restarted walk must preserve the original
            // durable baseline. Retry with a fresh instance, still proving replay
            // across restart rather than subsequent in-process cache reuse.
            Assert.Equal(baseline, await File.ReadAllBytesAsync(manifestPath));
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task RestartReusesManifestWhenJournalIsContinuous()
    {
        File.WriteAllText(Path.Combine(Root, "one.jpg"), "one");
        var journal = new FakeJournal();
        using (var first = Tracker(journal)) Assert.Equal(1, (await CompleteBaseline(first)).EnumeratedDirectories);
        var reused = await CompleteRestartedListing(journal);
        Assert.True(reused.ReusedPersistentManifest);
        Assert.True(reused.Complete);
        Assert.Equal(0, reused.EnumeratedDirectories);
        Assert.Single(reused.Files);
    }

    [Fact]
    public async Task RestartReenumeratesOnlyChangedDirectoryAndFindsAddedFile()
    {
        var a = Directory.CreateDirectory(Path.Combine(Root, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(Root, "b")).FullName;
        File.WriteAllText(Path.Combine(a, "one.jpg"), "one");
        File.WriteAllText(Path.Combine(b, "two.jpg"), "two");
        var journal = new FakeJournal();
        using (var first = Tracker(journal)) await CompleteBaseline(first);
        var added = Path.Combine(a, "new.jpg"); File.WriteAllText(added, "new"); journal.Change(added);
        var listing = await CompleteRestartedListing(journal);
        Assert.True(listing.ReusedPersistentManifest);
        Assert.True(listing.Complete);
        Assert.Equal(1, listing.EnumeratedDirectories);
        Assert.Equal(2, listing.ReusedDirectories);
        Assert.Contains(added, listing.Files);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("recreated")]
    [InlineData("truncated")]
    [InlineData("unknown_record")]
    [InlineData("corrupt_manifest")]
    public async Task UntrustedCheckpointAlwaysFallsBackToFullEnumeration(string failure)
    {
        File.WriteAllText(Path.Combine(Root, "one.jpg"), "one");
        var journal = new FakeJournal();
        using (var first = Tracker(journal)) await CompleteBaseline(first);
        switch (failure)
        {
            case "unavailable": journal.Available = false; break;
            case "recreated": journal.JournalId++; break;
            case "truncated": journal.Change(Path.Combine(Root, "one.jpg")); journal.FirstUsn = journal.NextUsn; break;
            case "unknown_record": journal.Change(Path.Combine(Root, "one.jpg")); journal.UnknownRange = true; break;
            case "corrupt_manifest": File.WriteAllText(Assert.Single(Directory.GetFiles(Cache, "*.scan")), "damaged"); break;
        }
        using var restarted = Tracker(journal);
        var listing = restarted.Read(Root, false, Photo, default);
        Assert.False(listing.ReusedPersistentManifest);
        Assert.Equal(1, listing.EnumeratedDirectories);
        Assert.Single(listing.Files);
    }

    [Fact]
    public async Task InterruptedImportKeepsPendingContentVerificationAcrossRestarts()
    {
        var path = Path.Combine(Root, "one.jpg"); await File.WriteAllBytesAsync(path, [1, 2]);
        var journal = new FakeJournal();
        DirectoryMediaScanner Scanner(DirectoryMediaChangeTracker tracker) => new((_, _, _) => Task.FromResult(new DirectoryMediaMetadata(10, 10)), tracker);
        DirectoryMediaPage initial;
        using (var first = Tracker(journal)) initial = await CompletePage(() => Scanner(first).ReadAsync(new(1, Root)));
        await File.WriteAllBytesAsync(path, [3, 4]); File.SetLastWriteTimeUtc(path, initial.Items[0].ModifiedUtc); journal.Change(path);
        DirectoryMediaPage changed;
        using (var second = Tracker(journal)) changed = await CompletePage(() => Scanner(second).ReadAsync(new(1, Root, KnownEntries: initial.Items)));
        Assert.False(changed.Items[0].Reused);
        Assert.NotEqual(initial.Items[0].Sha256, changed.Items[0].Sha256);
        // The caller never committed the changed page: old signatures must not be
        // accepted after the journal watermark itself has already been persisted.
        using (var third = Tracker(journal))
        {
            var retry = await CompletePage(() => Scanner(third).ReadAsync(new(1, Root, KnownEntries: initial.Items)));
            Assert.False(retry.Items[0].Reused); Assert.Equal(changed.Items[0].Sha256, retry.Items[0].Sha256);
        }
        using (var fourth = Tracker(journal)) await CompletePage(() => Scanner(fourth).ReadAsync(new(1, Root, KnownEntries: changed.Items)));
        using (var fifth = Tracker(journal)) Assert.True((await Scanner(fifth).ReadAsync(new(1, Root, KnownEntries: changed.Items))).Items[0].Reused);
    }

    [Fact]
    public async Task LostJournalRehashesEvenWhenSizeAndTimestampAreUnchanged()
    {
        var path = Path.Combine(Root, "one.jpg"); await File.WriteAllBytesAsync(path, [1, 2]);
        var journal = new FakeJournal();
        DirectoryMediaScanner Scanner(DirectoryMediaChangeTracker tracker) => new((_, _, _) => Task.FromResult(new DirectoryMediaMetadata(10, 10)), tracker);
        DirectoryMediaPage original;
        using (var first = Tracker(journal)) original = await CompletePage(() => Scanner(first).ReadAsync(new(1, Root)));
        using (var acknowledge = Tracker(journal)) await CompletePage(() => Scanner(acknowledge).ReadAsync(new(1, Root, KnownEntries: original.Items)));
        await File.WriteAllBytesAsync(path, [3, 4]); File.SetLastWriteTimeUtc(path, original.Items[0].ModifiedUtc);
        journal.Available = false;
        using var restarted = Tracker(journal);
        var result = await Scanner(restarted).ReadAsync(new(1, Root, KnownEntries: original.Items));
        Assert.False(result.Items[0].Reused);
        Assert.NotEqual(original.Items[0].Sha256, result.Items[0].Sha256);
    }

    [Fact]
    public async Task PurgeErasesManifestAndRejectsDelayedSaveFromAnotherStore()
    {
        var path = Path.Combine(Root, "one.jpg"); File.WriteAllText(path, "one");
        var journal = new FakeJournal();
        using var tracker = Tracker(journal);
        await CompleteBaseline(tracker);
        var otherProcess = new DirectoryScanManifestStore(Cache);
        var delayedSave = Assert.IsType<DirectoryScanManifest>(otherProcess.Load(Root));
        tracker.ForgetFile(path);
        Assert.Empty(Directory.GetFiles(Cache, "*.scan"));
        otherProcess.Save(delayedSave);
        Assert.Empty(Directory.GetFiles(Cache, "*.scan"));
        var fresh = tracker.Read(Root, false, Photo, default);
        Assert.Equal(1, fresh.EnumeratedDirectories);
        Assert.False(fresh.ReusedPersistentManifest);
    }

    [Fact]
    public async Task CanceledReadDoesNotAdvancePersistentCheckpoint()
    {
        File.WriteAllText(Path.Combine(Root, "one.jpg"), "one");
        var journal = new FakeJournal();
        using (var first = Tracker(journal)) await CompleteBaseline(first);
        var path = Assert.Single(Directory.GetFiles(Cache, "*.scan")); var before = File.ReadAllBytes(path);
        journal.Change(Path.Combine(Root, "one.jpg"));
        using var restarted = Tracker(journal);
        Assert.ThrowsAny<OperationCanceledException>(() => restarted.Read(Root, false, Photo, new CancellationToken(true)));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("load")]
    [InlineData("save")]
    [InlineData("purge")]
    public async Task AbandonedManifestTemporaryFilesAreRemovedWithoutDeletingUnrelatedFiles(string operation)
    {
        var photo = Path.Combine(Root, "one.jpg"); File.WriteAllText(photo, "one");
        var journal = new FakeJournal();
        using (var first = Tracker(journal)) await CompleteBaseline(first);
        var store = new DirectoryScanManifestStore(Cache);
        var manifest = Assert.IsType<DirectoryScanManifest>(store.Load(Root));
        var manifestPath = Assert.Single(Directory.GetFiles(Cache, "*.scan"));
        // Simulate termination before atomic replacement, including both a fully
        // written private manifest and a partially written/corrupt temporary file.
        var completeTemporary = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var partialTemporary = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.Copy(manifestPath, completeTemporary);
        File.WriteAllText(partialTemporary, photo);
        var unrelated = Path.Combine(Cache, "unrelated.tmp"); File.WriteAllText(unrelated, "keep");
        switch (operation)
        {
            case "load": Assert.NotNull(store.Load(Root)); break;
            case "save": store.Save(manifest); break;
            case "purge": store.ForgetFile(photo); Assert.Empty(Directory.GetFiles(Cache, "*.scan")); break;
        }
        Assert.False(File.Exists(completeTemporary));
        Assert.False(File.Exists(partialTemporary));
        Assert.Equal("keep", File.ReadAllText(unrelated));
    }

    [Fact]
    public void UsnParserInvalidatesKnownHardLinkEvenWhenReportedParentIsOutsideTree()
    {
        var path = Path.Combine(Root, "one.jpg");
        var name = Encoding.Unicode.GetBytes("outside.jpg");
        var data = new byte[(60 + name.Length + 7) & ~7];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8), 123);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(16), 999);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(24), 10);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(56), (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(58), 60); name.CopyTo(data, 60);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.True(NtfsDirectoryChangeJournal.ReadRecords(data, 11, new Dictionary<ulong, string[]>(), new Dictionary<ulong, string[]> { [123] = [path] }, directories, files));
        Assert.Contains(path, files); Assert.Contains(Root, directories);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 4);
        Assert.False(NtfsDirectoryChangeJournal.ReadRecords(data, 11, new Dictionary<ulong, string[]>(), new Dictionary<ulong, string[]>(), directories, files));
    }

    private sealed class FakeJournal : IDirectoryChangeJournal
    {
        private readonly Dictionary<string, ulong> _references = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(long Usn, string Path)> _changes = [];
        public bool Available = true;
        public bool UnknownRange;
        public ulong JournalId = 1;
        public long NextUsn = 1;
        public long FirstUsn;
        public void Change(string path) => _changes.Add((NextUsn++, path));
        public DirectoryJournalCheckpoint? Capture(string root) => Available ? new(1, GetReference(root)!.Value, JournalId, NextUsn, FirstUsn, FirstUsn) : null;
        public ulong? GetReference(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return null;
            if (!_references.TryGetValue(path, out var value)) _references[path] = value = (ulong)_references.Count + 1;
            return value;
        }
        public DirectoryJournalDelta ReadChanges(string root, DirectoryJournalCheckpoint from, DirectoryJournalCheckpoint to,
            IReadOnlyDictionary<string, ulong> directories, IReadOnlyDictionary<string, ulong> files, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!NtfsDirectoryChangeJournal.IsContinuous(from, to) || UnknownRange && from.NextUsn != to.NextUsn) return new(false, [], []);
            var paths = _changes.Where(change => change.Usn >= from.NextUsn && change.Usn < to.NextUsn).Select(change => change.Path).ToArray();
            return new(true, paths.Select(path => Path.GetDirectoryName(path)!).Distinct().ToArray(), paths);
        }
    }
}
