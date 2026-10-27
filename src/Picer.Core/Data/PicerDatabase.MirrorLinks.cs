using Picer.Core.Gateway;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    public void UpsertMirroredAlbumLinks(IEnumerable<GatewayAlbumImageLink> links, CancellationToken cancellationToken = default)
        => UpsertMirroredLinks(links.Select(link => (link.AlbumId, link.ImageId)), true, cancellationToken);

    public void UpsertMirroredTagLinks(IEnumerable<GatewayImageTagLink> links, CancellationToken cancellationToken = default)
        => UpsertMirroredLinks(links.Select(link => (link.TagId, link.ImageId)), false, cancellationToken);

    // These are remote projections, not new local edits. Keep each bounded batch
    // atomic and reuse one prepared command instead of flushing a transaction per link.
    private void UpsertMirroredLinks(IEnumerable<(long ParentId, long ImageId)> links, bool albums, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = BeginTransaction();
        using var command = Command(albums
            ? "INSERT OR IGNORE INTO album_images(album_id,image_id,added_utc) VALUES($parent,$image,$now)"
            : "INSERT OR IGNORE INTO image_tags(tag_id,image_id) VALUES($parent,$image)");
        command.Transaction = transaction;
        var parent = command.Parameters.AddWithValue("$parent", 0L);
        var image = command.Parameters.AddWithValue("$image", 0L);
        if (albums) command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        command.Prepare();
        foreach (var link in links)
        {
            cancellationToken.ThrowIfCancellationRequested();
            parent.Value = link.ParentId;
            image.Value = link.ImageId;
            command.ExecuteNonQuery();
        }
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }
}
