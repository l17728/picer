using Picer.Core.Data;
using Picer.Core.Models;
using Picer.IndexService.Identity;

namespace Picer.Tests;

public sealed class SpaceReadSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-read-cut-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore _accounts;
    private readonly AuthorizedDevice _device;
    private readonly PicerDatabase _database;
    private readonly SpaceReadSnapshots _snapshots;
    public SpaceReadSnapshotTests()
    {
        _accounts = new AccountStore(_root);
        var owner = _accounts.Register("read-owner", "read-snapshot-password!", "照片");
        var space = Assert.Single(_accounts.ListSpaces(owner));
        _device = new(Guid.NewGuid().ToString("N"), owner, space.Id, Guid.NewGuid().ToString("N"), "test-key", "gui", "index.read", 1, 1);
        _database = new(Path.Combine(_accounts.SpaceRoot(space.Id), "picer.db"));
        var source = _database.AddSource(@"C:\Photos");
        foreach (var id in new[] { 1, 2 }) _database.UpsertMirroredImage(new ImageRecord
        { Id = id, SourceId = source.Id, Path = $@"C:\Photos\{id}.jpg", FileSize = id, ModifiedUtc = DateTime.UtcNow });
        _snapshots = new(_accounts);
    }

    [Fact]
    public void PagingUsesOneCutDespiteConcurrentChangesAndBindsItToTheGrant()
    {
        var first = _snapshots.ReadChanges(_database, _device, new(PageSize: 1));
        Assert.True(first.HasMore); Assert.Single(first.Sources!);
        using (var update = _database.Command("UPDATE images SET file_size=99 WHERE id=2")) update.ExecuteNonQuery();
        var next = new Picer.Core.Gateway.GatewayChangeRequest(first.Epoch, first.Next, first.Through, 1, first.Snapshot);
        Assert.Throws<InvalidOperationException>(() => _snapshots.ReadChanges(_database, _device with { GrantId = Guid.NewGuid().ToString("N") }, next));
        var second = _snapshots.ReadChanges(_database, _device, next);
        Assert.False(second.HasMore); Assert.Equal(2, Assert.Single(second.Items).Image!.FileSize);
        Assert.Equal(99, _database.GetImage(2)!.FileSize);
    }

    [Fact]
    public void UnchangedRevisionDoesNotCopyDatabaseAgain()
    {
        var first = _snapshots.ReadChanges(_database, _device, new());
        var current = _snapshots.ReadChanges(_database, _device, new(first.Epoch, first.Through, MetadataRevision: first.MetadataRevision));
        Assert.Null(current.Snapshot); Assert.Empty(current.Items);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "read-snapshots"), "*.db"));
    }

    [Fact]
    public void PurgeInvalidatesOldCutAndKeepsSurvivorsInNewBaseline()
    {
        var first = _snapshots.ReadChanges(_database, _device, new());
        var guard = new RestoreGuard(Path.Combine(_root, "guard"), _accounts.ServiceId, true);
        guard.InitializeNewSpace(_device.SpaceId);
        new SpacePurgeCoordinator(_database, guard, _device.SpaceId).Purge("image", 1);
        Assert.Throws<InvalidOperationException>(() => _snapshots.ReadChanges(_database, _device, new(first.Epoch, 0, first.Through, Snapshot: first.Snapshot)));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "read-snapshots")));
        var remaining = _snapshots.ReadChanges(_database, _device, new());
        Assert.Equal(2, Assert.Single(remaining.Items).Id);
        Assert.False(remaining.Items[0].Deleted);
    }

    public void Dispose()
    { _snapshots.Dispose(); _database.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
