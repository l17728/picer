using Picer.Core.Data;
using Picer.Core.Identity;
using Picer.Core.Models;
using Picer.IndexService.Identity;
using Xunit;

namespace Picer.Tests;

public sealed class RestoreGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-restore-" + Guid.NewGuid().ToString("N"));
    private readonly string _space = Guid.NewGuid().ToString("N");
    private readonly string _service = Guid.NewGuid().ToString("N");
    private readonly RestoreGuard _guard;
    private readonly PicerDatabase _database;
    public RestoreGuardTests()
    {
        _guard = new(Path.Combine(_root, "separate-guard"), _service, true);
        _guard.InitializeNewSpace(_space);
        _database = new(Path.Combine(_root, "space", "picer.db"));
        var source = _database.AddSource(@"C:\Photos");
        _database.UpsertMirroredImage(new ImageRecord { Id = 1, SourceId = source.Id, Path = @"C:\Photos\private.jpg", FileSize = 1,
            ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow, Sha256 = new string('d', 64), ContentSha256 = new string('d', 64) });
    }

    [Fact]
    public void PurgeRemovesMediaAndTombstonesAndRejectsAnOlderBackup()
    {
        var backup = Path.Combine(_root, "older-backup.db");
        _database.BackupTo(backup);
        new SpacePurgeCoordinator(_database, _guard, _space).Purge("image", 1);
        Assert.Null(_database.GetImage(1));
        foreach (var table in new[] { "image_contents", "content_derivatives", "mirror_media_changes", "sync_entity_identities", "sync_outbox", "sync_operation_receipts", "sync_purge_journal" })
        {
            using var count = _database.Command("SELECT COUNT(*) FROM " + table);
            Assert.Equal(0L, count.ExecuteScalar());
        }
        _guard.VerifyCurrent(_space, 1);
        using var restored = new PicerDatabase(backup);
        Assert.Throws<InvalidDataException>(() => new SpacePurgeCoordinator(restored, _guard, _space).Recover());
        Assert.NotNull(restored.GetImage(1)); // A rejected restore is quarantined, never silently mutated into a valid baseline.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterruptedPurgeCompletesFromJournalBeforeServingReads(bool guardPrepared)
    {
        using (var journal = _database.Command("INSERT INTO sync_purge_journal VALUES(1,$operation,'image',1,1); UPDATE sync_space_state SET state='fencing' WHERE id=1;"))
        { journal.Parameters.AddWithValue("$operation", Guid.NewGuid().ToString("N")); journal.ExecuteNonQuery(); }
        if (guardPrepared) _guard.PreparePurge(_space, 0);
        new SpacePurgeCoordinator(_database, _guard, _space).Recover();
        Assert.Null(_database.GetImage(1));
        _guard.VerifyCurrent(_space, 1);
        using var journalCount = _database.Command("SELECT COUNT(*) FROM sync_purge_journal");
        Assert.Equal(0L, journalCount.ExecuteScalar());
    }

    [Fact]
    public void MissingGuardCannotBeReinitializedForAnExistingService()
    {
        Assert.Throws<InvalidDataException>(() => new RestoreGuard(Path.Combine(_root, "missing-guard"), _service, false));
        Assert.Throws<InvalidDataException>(() => new RestoreGuard(Path.Combine(_root, "separate-guard"), Guid.NewGuid().ToString("N"), false));
    }

    public void Dispose()
    {
        _database.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
