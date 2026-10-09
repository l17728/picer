using Picer.Core.Gateway;
using Picer.Core.Video;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    public GatewayChangePage ReadMirrorChanges(GatewayChangeRequest request)
    {
        if (request.After < 0 || request.Through < 0) throw new ArgumentOutOfRangeException(nameof(request));
        using var transaction = _conn.BeginTransaction(deferred: true);
        string epoch;
        long revision, metadata;
        using (var state = Command("SELECT epoch,revision,metadata_revision FROM mirror_revision WHERE id=1"))
        using (var reader = state.ExecuteReader())
        {
            reader.Read(); epoch = reader.GetString(0); revision = reader.GetInt64(1); metadata = reader.GetInt64(2);
        }
        if ((request.Epoch is not null && request.Epoch != epoch) || request.After > revision || request.Through > revision)
            return new(epoch, revision, 0, false, true, metadata, []);
        var through = request.Through ?? revision;
        if (through < request.After) throw new ArgumentOutOfRangeException(nameof(request));
        var size = Math.Clamp(request.PageSize, 1, 500);
        var rows = new List<(long Revision, string Kind, long Id, long Source, bool Deleted)>();
        using (var changes = Command("SELECT revision,kind,media_id,source_id,deleted FROM mirror_media_changes WHERE revision>$after AND revision<=$through ORDER BY revision LIMIT $limit"))
        {
            changes.Parameters.AddWithValue("$after", request.After);
            changes.Parameters.AddWithValue("$through", through);
            changes.Parameters.AddWithValue("$limit", size + 1);
            using var reader = changes.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetBoolean(4)));
        }
        var more = rows.Count > size;
        var items = new List<GatewayMediaChange>();
        var repository = new VideoRepository(this);
        foreach (var row in rows.Take(size))
        {
            var photo = !row.Deleted && row.Kind == "image" ? GetImage(row.Id) : null;
            var video = !row.Deleted && row.Kind == "video" ? repository.Get(row.Id) : null;
            items.Add(new(row.Revision, row.Kind, row.Id, row.Source, row.Deleted, photo, video));
        }
        transaction.Commit();
        return new(epoch, through, more ? items[^1].Revision : through, more, false, metadata, items);
    }

    public MirrorCheckpoint? GetMirrorCheckpoint()
    {
        using var command = Command("SELECT epoch,media_revision,metadata_revision FROM mirror_checkpoint WHERE id=1");
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)) : null;
    }

    public void SaveMirrorCheckpoint(MirrorCheckpoint checkpoint)
    {
        using var command = Command("INSERT INTO mirror_checkpoint VALUES(1,$epoch,$media,$metadata) ON CONFLICT(id) DO UPDATE SET epoch=excluded.epoch,media_revision=excluded.media_revision,metadata_revision=excluded.metadata_revision");
        command.Parameters.AddWithValue("$epoch", checkpoint.Epoch);
        command.Parameters.AddWithValue("$media", checkpoint.MediaRevision);
        command.Parameters.AddWithValue("$metadata", checkpoint.MetadataRevision);
        command.ExecuteNonQuery();
    }
}
