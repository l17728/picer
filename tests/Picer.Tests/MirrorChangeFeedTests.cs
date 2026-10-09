using System.Net;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Models;
using Picer.Core.Search;

namespace Picer.Tests;

public sealed class MirrorChangeFeedTests
{
    [Fact]
    public void SameCountRenameMissingAndPurgeProduceLatestChangesWithoutPrivateTombstoneData()
    {
        using var database = new PicerDatabase(":memory:");
        var source = database.AddSource(Path.GetFullPath("feed-fixture"));
        database.UpsertMirroredImage(new() { Id = 10, SourceId = source.Id, Path = Path.GetFullPath("feed-fixture/old.jpg"), FileSize = 2, ModifiedUtc = DateTime.UtcNow });
        var initial = database.ReadMirrorChanges(new());
        Assert.Single(initial.Items);
        using (var update = database.Command("UPDATE images SET path=$path,status='missing' WHERE id=10"))
        {
            update.Parameters.AddWithValue("$path", Path.GetFullPath("feed-fixture/moved.jpg")); update.ExecuteNonQuery();
        }
        var renamed = database.ReadMirrorChanges(new(initial.Epoch, initial.Next));
        Assert.Equal(ImageStatus.Missing, Assert.Single(renamed.Items).Image!.Status);
        Assert.EndsWith("moved.jpg", renamed.Items[0].Image!.Path);
        database.DeleteImage(10);
        var purged = database.ReadMirrorChanges(new(initial.Epoch, renamed.Next));
        var deletion = Assert.Single(purged.Items);
        Assert.True(deletion.Deleted); Assert.Null(deletion.Image); Assert.Null(deletion.Video);
        Assert.Empty(database.ReadMirrorChanges(new(initial.Epoch, purged.Next)).Items);
    }

    [Fact]
    public void BoundedPagesDoNotLoseMutationsBetweenPages()
    {
        using var database = new PicerDatabase(":memory:");
        var source = database.AddSource(Path.GetFullPath("feed-pages"));
        for (var id = 1; id <= 4; id++) database.UpsertMirroredImage(new() { Id = id, SourceId = source.Id, Path = Path.GetFullPath($"feed-pages/{id}.jpg"), FileSize = 2, ModifiedUtc = DateTime.UtcNow });
        var first = database.ReadMirrorChanges(new(PageSize: 1));
        Assert.True(first.HasMore);
        using (var update = database.Command("UPDATE images SET status='missing' WHERE id=4")) update.ExecuteNonQuery();
        var current = first;
        while (current.HasMore) current = database.ReadMirrorChanges(new(first.Epoch, current.Next, first.Through, 1));
        Assert.Equal(first.Through, current.Next);
        var afterSnapshot = database.ReadMirrorChanges(new(first.Epoch, current.Next));
        Assert.Equal(4, Assert.Single(afterSnapshot.Items).Id);
        Assert.Equal(ImageStatus.Missing, afterSnapshot.Items[0].Image!.Status);
    }

    [Fact]
    public void MetadataChangesAndServerResetAreIndependentOfMediaCount()
    {
        using var database = new PicerDatabase(":memory:");
        var initial = database.ReadMirrorChanges(new());
        database.UpsertMirroredTag(new Tag { Id = 1, Name = "fixture" });
        var changed = database.ReadMirrorChanges(new(initial.Epoch, initial.Next));
        Assert.Empty(changed.Items);
        Assert.True(changed.MetadataRevision > initial.MetadataRevision);
        Assert.True(database.ReadMirrorChanges(new("different-epoch", 0)).ResetRequired);
        Assert.True(database.ReadMirrorChanges(new(initial.Epoch, long.MaxValue)).ResetRequired);
        Assert.Throws<ArgumentOutOfRangeException>(() => database.ReadMirrorChanges(new(After: -1)));
    }

    [Fact]
    public void CheckpointSurvivesRestartWithoutAcknowledgingPendingMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-feed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "mirror.db");
            var checkpoint = new MirrorCheckpoint("server-epoch", 50, 7);
            using (var database = new PicerDatabase(path)) database.SaveMirrorCheckpoint(checkpoint);
            using (var database = new PicerDatabase(path)) Assert.Equal(checkpoint, database.GetMirrorCheckpoint());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AuthoritativeUnlinkOnlyRemovesMirrorOwnedRelationships()
    {
        using var database = new PicerDatabase(":memory:");
        var source = database.AddSource(Path.GetFullPath("feed-links"));
        foreach (var id in new[] { 1, 2 })
            database.UpsertMirroredImage(new() { Id = id, SourceId = source.Id, Path = Path.GetFullPath($"feed-links/{id}.jpg"), FileSize = 2, ModifiedUtc = DateTime.UtcNow });
        database.UpsertMirroredTag(new Tag { Id = 1, Name = "fixture" });
        database.UpsertMirroredImageTag(1, 1);
        database.UpsertMirroredImageTag(2, 1);
        using (var own = database.Command("INSERT INTO mirror_owned_media VALUES('image',1,'fixture')")) own.ExecuteNonQuery();
        database.ReconcileMirroredPhotoMetadata(new([], [], []));
        using var query = database.Command("SELECT image_id FROM image_tags");
        Assert.Equal(2L, query.ExecuteScalar());
    }

    [Fact]
    public async Task OlderHttpServiceFallsBackExplicitlyInsteadOfTreatingCountsAsFreshness()
    {
        using var client = new HttpClient(new LegacyHandler());
        using var gateway = new HttpIndexGateway("https://fixture.invalid", "fixture", client: client);
        await Assert.ThrowsAsync<NotSupportedException>(() => gateway.GetChangesAsync(new()));
    }

    private sealed class LegacyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/api/gateway/v1/changes", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
