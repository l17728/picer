using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Gateway;

namespace Picer.Tests;

public sealed class MirrorLinkBatchTests
{
    private static PicerDatabase Create()
    {
        var db = new PicerDatabase(":memory:");
        var source = db.AddSource(Path.GetFullPath("mirror-link-fixture"));
        for (var id = 1; id <= 2; id++)
            db.UpsertMirroredImage(new() { Id = id, SourceId = source.Id, Path = Path.GetFullPath($"mirror-link-fixture/{id}.jpg"), ModifiedUtc = DateTime.UtcNow });
        using var seed = db.Command("INSERT INTO albums(id,name,created_utc) VALUES(1,'fixture','2026-10-27'); INSERT INTO tags(id,name,created_utc) VALUES(1,'fixture','2026-10-27');");
        seed.ExecuteNonQuery();
        return db;
    }

    private static void Apply(PicerDatabase db, bool albums, IEnumerable<long> ids, CancellationToken token = default)
    {
        if (albums) db.UpsertMirroredAlbumLinks(ids.Select(id => new GatewayAlbumImageLink(1, id)), token);
        else db.UpsertMirroredTagLinks(ids.Select(id => new GatewayImageTagLink(id, 1)), token);
    }

    private static long Count(PicerDatabase db, bool albums)
    {
        using var command = db.Command(albums ? "SELECT COUNT(*) FROM album_images" : "SELECT COUNT(*) FROM image_tags");
        return (long)command.ExecuteScalar()!;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReplayIsIdempotentAndDoesNotCreateLocalEdits(bool albums)
    {
        using var db = Create();
        Apply(db, albums, [1, 2, 1]);
        Apply(db, albums, [2, 1]);
        Assert.Equal(2, Count(db, albums));
        Assert.Empty(db.GetMetadataEdits());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvalidReferenceRollsBackTheWholeBatch(bool albums)
    {
        using var db = Create();
        Assert.Throws<SqliteException>(() => Apply(db, albums, [1, 999]));
        Assert.Equal(0, Count(db, albums));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancellationRollsBackAndAllowsAnIdempotentRetry(bool albums)
    {
        using var db = Create();
        using var cancellation = new CancellationTokenSource();
        IEnumerable<long> CancelAfterFirst()
        {
            yield return 1;
            cancellation.Cancel();
            yield return 2;
        }
        Assert.ThrowsAny<OperationCanceledException>(() => Apply(db, albums, CancelAfterFirst(), cancellation.Token));
        Assert.Equal(0, Count(db, albums));
        Apply(db, albums, [1, 2]);
        Assert.Equal(2, Count(db, albums));
    }
}
