using Picer.Core.Data;
using Picer.Core.Indexing;
using Picer.Core.Models;
using Picer.Core.Video;

namespace Picer.Tests;

public sealed class DirectoryScanPagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-scan-page-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _db;
    private readonly long _source;
    public DirectoryScanPagingTests() { _db = new(Path.Combine(_root, "picer.db")); _source = _db.AddSource(_root).Id; }
    public void Dispose() { _db.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
    private ImageRecord Add(string name) => _db.UpsertImage(new() { SourceId = _source, Path = Path.Combine(_root, name), FileSize = 3,
        ModifiedUtc = DateTime.UtcNow.AddDays(-1), IndexedUtc = DateTime.UtcNow, Sha256 = new string('a', 64), Width = 10, Height = 10 });

    [Fact]
    public void PageQueriesReturnOnlyRequestedRecordsWithSeparatorAndCaseCompatibility()
    {
        var first = Add("ONE.jpg"); var second = Add("two.jpg"); Add("unrelated.jpg");
        var rows = _db.GetScanImagesByPaths(_source, [first.Path.ToLowerInvariant().Replace('\\', '/'), second.Path, second.Path]);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, record => record.Id == first.Id);
        Assert.Contains(rows, record => record.Id == second.Id);
        Assert.Empty(_db.GetScanImagesByPaths(_source + 999, [first.Path]));
        Assert.Empty(_db.GetScanImagesByPaths(_source, []));
        var repository = new VideoRepository(_db);
        repository.UpsertRemote(new(0, _source, Path.Combine(_root, "ONE.mp4"), 3, DateTime.UtcNow, 1, 10, 10, null, null, "active"), _source);
        repository.UpsertRemote(new(0, _source, Path.Combine(_root, "other.mp4"), 3, DateTime.UtcNow, 1, 10, 10, null, null, "active"), _source);
        Assert.Single(_db.GetScanVideosByPaths(_source, [Path.Combine(_root, "one.mp4").Replace('\\', '/')]));
    }

    [Fact]
    public void MissingReconciliationUsesInitialSnapshotAndRechecksChangedIdentity()
    {
        var present = Add("present.jpg"); var missing = Add("missing.jpg"); var changed = Add("changed.jpg");
        var first = new DirectoryMediaPage("paging", 2, 1, false, [], 0, [present.Path], [], true);
        DirectoryMediaImport.Apply(_db, _source, _root, first);
        var addedLater = Add("arrived-after-snapshot.jpg");
        using (var change = _db.Command("UPDATE images SET file_size=4 WHERE id=$id"))
        { change.Parameters.AddWithValue("$id", changed.Id); change.ExecuteNonQuery(); }
        DirectoryMediaImport.Apply(_db, _source, _root, new("paging", 2, 2, true, [], 1, [Path.Combine(_root, "other-observed.jpg")], [], true));
        Assert.Equal(ImageStatus.Active, _db.GetImage(present.Id)!.Status);
        Assert.Equal(ImageStatus.Missing, _db.GetImage(missing.Id)!.Status);
        Assert.Equal(ImageStatus.Active, _db.GetImage(changed.Id)!.Status);
        Assert.Equal(ImageStatus.Active, _db.GetImage(addedLater.Id)!.Status);
    }

    [Fact]
    public void ReplayedFirstPageCannotReplaceInitialMissingSnapshot()
    {
        var missing = Add("missing.jpg");
        var first = new DirectoryMediaPage("replay", 2, 1, false, [], 0, [Path.Combine(_root, "one.jpg")], [], true);
        DirectoryMediaImport.Apply(_db, _source, _root, first);
        Add("added.jpg");
        DirectoryMediaImport.Apply(_db, _source, _root, first);
        DirectoryMediaImport.Apply(_db, _source, _root, new("replay", 2, 2, true, [], 1, [Path.Combine(_root, "two.jpg")], [], true));
        Assert.Equal(ImageStatus.Active, _db.GetImage(missing.Id)!.Status);
    }
}
