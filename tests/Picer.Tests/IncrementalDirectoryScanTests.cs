using Picer.Core.Indexing;
using Picer.Core.Data;

namespace Picer.Tests;

public sealed class IncrementalDirectoryScanTests
{
    [Fact]
    public async Task UnchangedScanDoesNotWriteIndexButMissingFileDoes()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-scan-writes-" + Guid.NewGuid().ToString("N"));
        var mediaRoot = Directory.CreateDirectory(Path.Combine(root, "photos")).FullName;
        var path = Path.Combine(mediaRoot, "one.jpg");
        await File.WriteAllBytesAsync(path, [1, 2]);
        using var db = new PicerDatabase(Path.Combine(root, "test.db"));
        var source = db.AddSource(mediaRoot);
        // Keep database/WAL writes outside the watched media tree and isolate the
        // tracker from other parallel tests evicting its four-root shared cache.
        using var tracker = new DirectoryMediaChangeTracker();
        var scanner = new DirectoryMediaScanner((_, _, _) => Task.FromResult(new DirectoryMediaMetadata(32, 32)), tracker);
        var first = await scanner.ReadAsync(new(source.Id, mediaRoot));
        Assert.True(DirectoryMediaImport.ApplyAndDetectChanges(db, source.Id, mediaRoot, first));
        var known = DirectoryMediaImport.KnownEntries(db, source.Id, mediaRoot);
        var second = await scanner.ReadAsync(new(source.Id, mediaRoot, KnownEntries: known));
        Assert.False(DirectoryMediaImport.ApplyAndDetectChanges(db, source.Id, mediaRoot, second));
        File.Delete(path);
        var third = await scanner.ReadAsync(new(source.Id, mediaRoot, KnownEntries: known));
        var settling = System.Diagnostics.Stopwatch.StartNew();
        while ((!third.EnumerationComplete || third.Total != 0) && settling.Elapsed < TimeSpan.FromSeconds(3))
        {
            Assert.False(DirectoryMediaImport.ApplyAndDetectChanges(db, source.Id, mediaRoot, third));
            Assert.Equal(Picer.Core.Models.ImageStatus.Active, Assert.Single(db.GetImages(includeMissing: true)).Status);
            await Task.Delay(25);
            third = await scanner.ReadAsync(new(source.Id, mediaRoot, KnownEntries: known));
        }
        Assert.True(third.EnumerationComplete, "The deletion scan did not become complete after watcher notifications settled.");
        Assert.Equal(0, third.Total);
        Assert.True(DirectoryMediaImport.ApplyAndDetectChanges(db, source.Id, mediaRoot, third));
        Assert.Equal(Picer.Core.Models.ImageStatus.Missing, Assert.Single(db.GetImages(includeMissing: true)).Status);
        var fourth = await scanner.ReadAsync(new(source.Id, mediaRoot, KnownEntries: known));
        Assert.False(DirectoryMediaImport.ApplyAndDetectChanges(db, source.Id, mediaRoot, fourth));
    }

    [Fact]
    public async Task NewRunUsesIndexedSignaturesButDetectsChangesAndHonorsFullVerification()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-incremental-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var photo = Path.Combine(root, "photo.jpg"); var video = Path.Combine(root, "video.mp4");
            await File.WriteAllBytesAsync(photo, [1, 2]); await File.WriteAllBytesAsync(video, [3, 4]);
            var calls = 0;
            using var tracker = new DirectoryMediaChangeTracker();
            DirectoryMediaScanner Scanner() => new((_, _, _) => { calls++; return Task.FromResult(new DirectoryMediaMetadata(32, 32)); }, tracker);
            var first = await Scanner().ReadAsync(new(1, root));
            Assert.Equal(2, calls);
            var second = await Scanner().ReadAsync(new(1, root, KnownEntries: first.Items));
            Assert.True(second.Done); Assert.Equal(2, calls);
            Assert.All(second.Items, entry => Assert.True(entry.Reused));
            await File.WriteAllBytesAsync(photo, [1, 2, 3]);
            await File.WriteAllBytesAsync(Path.Combine(root, "added.jpg"), [5]);
            File.Delete(video);
            var third = await Scanner().ReadAsync(new(1, root, KnownEntries: second.Items));
            Assert.Equal(4, calls); Assert.Equal(2, third.Total);
            Assert.DoesNotContain(video, third.ObservedPaths!);
            Assert.NotEqual(first.Items.Single(e => !e.IsVideo).Sha256, third.Items.Single(e => e.Path == photo).Sha256);
            var forced = await Scanner().ReadAsync(new(1, root, ForceFullScan: true, KnownEntries: third.Items));
            Assert.Equal(6, calls); Assert.All(forced.Items, entry => Assert.False(entry.Reused));
        }
        finally { Directory.Delete(root, true); }
    }
}
