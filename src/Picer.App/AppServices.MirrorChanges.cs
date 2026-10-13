using System.IO;
using Picer.App.Infrastructure;
using Picer.Core.Gateway;
using Picer.Core.Models;
using Picer.Core.Data;
using Picer.Core.Video;

namespace Picer.App;

public static partial class AppServices
{
    internal sealed record MirrorRefresh(bool Supported, MirrorCheckpoint? Checkpoint, long MetadataRevision);

    internal static async Task<MirrorRefresh> RefreshChangedMediaAsync(IIndexGateway gateway,
        IReadOnlyList<SourceFolder> sources, CancellationToken cancellationToken)
    {
        var checkpoint = await UiWork.RunDatabaseAsync("RemoteMirror.Checkpoint", Db.GetMirrorCheckpoint, cancellationToken).ConfigureAwait(false);
        GatewayChangePage page;
        try { page = await gateway.GetChangesAsync(new(checkpoint?.Epoch, checkpoint?.MediaRevision ?? 0, MetadataRevision: checkpoint?.MetadataRevision), cancellationToken).ConfigureAwait(false); }
        catch (NotSupportedException) { return new(false, null, 0); }
        var reset = checkpoint is null || page.ResetRequired;
        if (page.ResetRequired)
            page = await gateway.GetChangesAsync(new(), cancellationToken).ConfigureAwait(false);
        if (page.ResetRequired) throw new InvalidDataException("mirror_epoch_reset_failed");
        sources = page.Sources ?? sources;
        var sourceMap = new Dictionary<long, long>();
        foreach (var source in sources)
            sourceMap[source.Id] = await UiWork.RunDatabaseAsync("RemoteMirror.Source", () => Db.AddSource(source.Path, reconcileAliases: true).Id, cancellationToken).ConfigureAwait(false);
        var epoch = page.Epoch;
        var through = page.Through;
        var metadataRevision = page.MetadataRevision;
        var seenEpoch = reset ? Guid.NewGuid().ToString("N") : epoch;
        if (reset && ServiceMode)
            await UiWork.RunDatabaseAsync("RemoteMirror.TrackLegacy", () =>
            {
                using var track = Db.Command("INSERT OR IGNORE INTO mirror_owned_media SELECT 'image',id,'' FROM images; INSERT OR IGNORE INTO mirror_owned_media SELECT 'video',id,'' FROM videos;");
                track.ExecuteNonQuery();
            }, cancellationToken).ConfigureAwait(false);
        long after = reset ? 0 : checkpoint!.MediaRevision;
        while (true)
        {
            if (page.Epoch != epoch || page.Through != through || page.ResetRequired || page.Next < after || (page.HasMore && page.Next == after))
                throw new InvalidDataException("mirror_change_page_inconsistent");
            foreach (var chunk in page.Items.Chunk(50))
            {
                foreach (var deleted in chunk.Where(item => item.Deleted))
                    await RemoveMirroredReferenceAsync(deleted.Kind, deleted.Id, cancellationToken).ConfigureAwait(false);
                await UiWork.RunDatabaseAsync("RemoteMirror.ApplyChanges", () =>
                {
                    foreach (var change in chunk)
                    {
                        if (change.Kind is not ("image" or "video")) throw new InvalidDataException("mirror_media_kind_invalid");
                        if (change.Deleted)
                        {
                            continue;
                        }
                        if (!sourceMap.TryGetValue(change.SourceId, out var sourceId))
                            throw new InvalidDataException("mirror_source_changed_retry");
                        if (change.Kind == "image" && change.Image is { } image)
                        {
                            ValidateLocalMirrorIdentity("image", image.Id, image.Path);
                            image.SourceId = sourceId;
                            Db.UpsertMirroredImage(image);
                        }
                        else if (change.Kind == "video" && change.Video is { } video)
                        {
                            ValidateLocalMirrorIdentity("video", video.Id, video.Path);
                            Videos.UpsertMirrored(video, sourceId);
                        }
                        else throw new InvalidDataException("mirror_change_payload_missing");
                        using var own = Db.Command("INSERT INTO mirror_owned_media VALUES($kind,$id,$seen) ON CONFLICT(kind,media_id) DO UPDATE SET seen_epoch=excluded.seen_epoch");
                        own.Parameters.AddWithValue("$kind", change.Kind); own.Parameters.AddWithValue("$id", change.Id);
                        own.Parameters.AddWithValue("$seen", seenEpoch); own.ExecuteNonQuery();
                    }
                }, cancellationToken).ConfigureAwait(false);
            }
            after = page.Next;
            if (!page.HasMore) break;
            page = await gateway.GetChangesAsync(new(epoch, after, through, Snapshot: page.Snapshot), cancellationToken).ConfigureAwait(false);
        }
        var updated = new MirrorCheckpoint(epoch, through, reset ? 0 : checkpoint!.MetadataRevision);
        if (reset)
        {
            var stale = await UiWork.RunDatabaseAsync("RemoteMirror.StaleReferences", () =>
            {
                var items = new List<(string Kind, long Id)>();
                using (var query = Db.Command("SELECT kind,media_id FROM mirror_owned_media WHERE seen_epoch<>$seen"))
                {
                    query.Parameters.AddWithValue("$seen", seenEpoch);
                    using var reader = query.ExecuteReader();
                    while (reader.Read()) items.Add((reader.GetString(0), reader.GetInt64(1)));
                }
                return items;
            }, cancellationToken).ConfigureAwait(false);
            foreach (var item in stale)
            {
                // The compact change table may move an unread row past Through during
                // bootstrap. Absence from those pages is never evidence of deletion.
                var image = item.Kind == "image" ? await gateway.GetImageAsync(item.Id, cancellationToken).ConfigureAwait(false) : null;
                var video = item.Kind == "video" ? await gateway.GetVideoAsync(item.Id, cancellationToken).ConfigureAwait(false) : null;
                if (image is null && video is null)
                {
                    await RemoveMirroredReferenceAsync(item.Kind, item.Id, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var remoteSource = image?.SourceId ?? video!.SourceId;
                if (!sourceMap.TryGetValue(remoteSource, out var localSource))
                {
                    var latestSources = await gateway.ListSourcesAsync(cancellationToken).ConfigureAwait(false);
                    var source = latestSources.FirstOrDefault(value => value.Id == remoteSource)
                        ?? throw new InvalidDataException("mirror_source_changed_retry");
                    localSource = await UiWork.RunDatabaseAsync("RemoteMirror.NewSource", () => Db.AddSource(source.Path, reconcileAliases: true).Id, cancellationToken).ConfigureAwait(false);
                    sourceMap[remoteSource] = localSource;
                }
                await UiWork.RunDatabaseAsync("RemoteMirror.ReconcileLiveReference", () =>
                {
                    ValidateLocalMirrorIdentity(item.Kind, item.Id, image?.Path ?? video!.Path);
                    if (image is not null) { image.SourceId = localSource; Db.UpsertMirroredImage(image); }
                    else Videos.UpsertMirrored(video!, localSource);
                }, cancellationToken).ConfigureAwait(false);
            }
        }
        // Never checkpoint an incomplete page or a failed cleanup. Replay is idempotent.
        await UiWork.RunDatabaseAsync("RemoteMirror.CommitCheckpoint", () => Db.SaveMirrorCheckpoint(updated), cancellationToken).ConfigureAwait(false);
        return new(true, updated, metadataRevision);
    }

    private static async Task RemoveMirroredReferenceAsync(string kind, long id, CancellationToken token)
    {
        if (kind is not ("image" or "video")) throw new InvalidDataException("mirror_media_kind_invalid");
        var path = kind == "video"
            ? await UiWork.RunDatabaseAsync("RemoteMirror.PurgeSnapshot", () => Videos.Get(id)?.Path, token).ConfigureAwait(false)
            : null;
        using var lifetime = path is null ? null : await VideoOperationLifetime.BeginPurgeAsync(path, token).ConfigureAwait(false);
        await UiWork.RunDatabaseAsync("RemoteMirror.PurgeReference", () => RemoveMirroredReference(kind, id), token).ConfigureAwait(false);
    }

    private static void ValidateLocalMirrorIdentity(string kind, long id, string path)
    {
        if (ServiceMode) return;
        // Local and remote databases can allocate unrelated numeric IDs. Never
        // replace a local reference merely because a remote ID happens to match.
        var currentPath = kind == "image" ? Db.GetImage(id)?.Path : Videos.Get(id)?.Path;
        var normalized = Picer.Core.Indexing.DirectoryMediaImport.Normalize(path);
        if (currentPath is not null && !Picer.Core.Indexing.DirectoryMediaImport.Normalize(currentPath).Equals(normalized, StringComparison.OrdinalIgnoreCase))
        {
            using var own = Db.Command("SELECT 1 FROM mirror_owned_media WHERE kind=$kind AND media_id=$id");
            own.Parameters.AddWithValue("$kind", kind); own.Parameters.AddWithValue("$id", id);
            if (own.ExecuteScalar() is null) throw new InvalidDataException("本地与远端索引 ID 冲突；请使用服务索引模式，现有本地记录已保留。");
        }
        using var collision = Db.Command(kind == "image"
            ? "SELECT 1 FROM images WHERE path=$path AND id<>$id AND id NOT IN (SELECT media_id FROM mirror_owned_media WHERE kind='image') LIMIT 1"
            : "SELECT 1 FROM videos WHERE path=$path AND id<>$id AND id NOT IN (SELECT media_id FROM mirror_owned_media WHERE kind='video') LIMIT 1");
        collision.Parameters.AddWithValue("$path", path); collision.Parameters.AddWithValue("$id", id);
        if (collision.ExecuteScalar() is not null) throw new InvalidDataException("本地与远端路径映射冲突；请使用服务索引模式，现有本地记录已保留。");
    }

    private static void RemoveMirroredReference(string kind, long id)
    {
        using var owned = Db.Command("SELECT 1 FROM mirror_owned_media WHERE kind=$kind AND media_id=$id");
        owned.Parameters.AddWithValue("$kind", kind); owned.Parameters.AddWithValue("$id", id);
        if (owned.ExecuteScalar() is null && !ServiceMode) return;
        if (kind == "image") new ImageRecordPurger(Db).Purge(id, requireMissing: false);
        else new VideoRecordPurger(Db, ServiceMode ? VideoPurgeCacheScope.Remote : VideoPurgeCacheScope.Local).Purge(id, requireMissing: false);
        using var forget = Db.Command("DELETE FROM mirror_owned_media WHERE kind=$kind AND media_id=$id");
        forget.Parameters.AddWithValue("$kind", kind); forget.Parameters.AddWithValue("$id", id); forget.ExecuteNonQuery();
    }
}
