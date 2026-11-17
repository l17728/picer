using System.Text.Json;
using Picer.Core.Gateway;
using Picer.Core.Video;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    public DescriptionDeltaCursor? GetDescriptionDeltaCursor()
    {
        using var query = Command("SELECT epoch,scope,sequence FROM description_delta_checkpoint WHERE id=1");
        using var row = query.ExecuteReader();
        return row.Read() ? new(row.GetString(0), row.GetString(1), row.GetInt64(2)) : null;
    }

    public DescriptionDeltaPage ReadDescriptionDelta(DescriptionDeltaRequest request, string scope)
    {
        using var transaction = BeginReadTransaction();
        using (var state = Command("SELECT state FROM sync_space_state WHERE id=1"))
            if (state.ExecuteScalar() as string != "active") throw new InvalidOperationException("description_delta_baseline_required");
        var epoch = DescriptionEpoch();
        var after = request.Cursor?.Sequence ?? 0;
        if (after < 0 || request.Cursor is { } cursor && (cursor.Epoch != epoch || cursor.Scope != scope))
            throw new InvalidOperationException("description_delta_baseline_required");
        using var maximum = Command("SELECT COALESCE(MAX(sequence),0) FROM description_changes WHERE epoch=$epoch");
        maximum.Parameters.AddWithValue("$epoch", epoch);
        var high = Convert.ToInt64(maximum.ExecuteScalar());
        var through = request.Through ?? high;
        if (through > high || after > through) throw new InvalidOperationException("description_delta_baseline_required");
        using var query = Command("SELECT sequence,deleted,payload FROM description_changes WHERE epoch=$epoch AND sequence>$after AND sequence<=$through ORDER BY sequence LIMIT 20");
        query.Parameters.AddWithValue("$epoch", epoch); query.Parameters.AddWithValue("$after", after); query.Parameters.AddWithValue("$through", through);
        var entries = new List<DescriptionDeltaEntry>(); long bytes = 0;
        using (var rows = query.ExecuteReader())
        {
            while (rows.Read())
            {
                var payload = rows.GetString(2);
                bytes += System.Text.Encoding.UTF8.GetByteCount(payload);
                if (entries.Count > 0 && bytes > 4 * 1024 * 1024) break;
                entries.Add(new(rows.GetInt64(0), rows.GetInt64(1) != 0, JsonSerializer.Deserialize<DescriptionVersion>(payload)!));
            }
        }
        var next = entries.Count == 0 ? through : entries[^1].Sequence;
        transaction.Commit();
        return new(new(epoch, scope, next), after, through, entries);
    }

    public void ApplyDescriptionDelta(DescriptionDeltaPage page, DescriptionDeltaCursor? expected)
    {
        using var transaction = BeginTransaction();
        if (GetDescriptionDeltaCursor() != expected || page.After != (expected?.Sequence ?? 0) ||
            GetMirrorCheckpoint()?.Epoch != page.Cursor.Epoch ||
            expected is not null && (expected.Epoch != page.Cursor.Epoch || expected.Scope != page.Cursor.Scope) ||
            page.Cursor.Sequence < page.After || page.Cursor.Sequence > page.Through)
            throw new InvalidOperationException("description_delta_baseline_required");
        var previous = page.After;
        foreach (var entry in page.Entries)
        {
            if (entry.Sequence <= previous || entry.Sequence > page.Cursor.Sequence) throw new InvalidDataException("description_delta_order_invalid");
            previous = entry.Sequence;
            var version = entry.Version;
            if (version.Kind is not ("image" or "video") || version.MediaId <= 0) throw new InvalidDataException("description_delta_identity_invalid");
            if (!entry.Deleted)
            {
                if (!IsCurrentDescriptionVersion(version)) throw new InvalidOperationException("description_delta_media_refresh_required");
                using (var restore = Command("DELETE FROM description_delta_tombstones WHERE epoch=$epoch AND version_id=$version"))
                {
                    restore.Parameters.AddWithValue("$epoch", page.Cursor.Epoch); restore.Parameters.AddWithValue("$version", version.VersionId); restore.ExecuteNonQuery();
                }
                StoreDescriptionVersion(version);
                var confirmedVersion = version.Video is { } summary
                    ? CreateDescriptionVersion("video", CanonicalDescriptionVideo(summary).VideoId, version.Sha256, null, CanonicalDescriptionVideo(summary)).VersionId
                    : version.VersionId;
                using (var seen = Command("INSERT OR IGNORE INTO description_delta_seen VALUES($epoch,$version)"))
                {
                    seen.Parameters.AddWithValue("$epoch", page.Cursor.Epoch); seen.Parameters.AddWithValue("$version", confirmedVersion); seen.ExecuteNonQuery();
                }
                UpsertMirroredVideoMetadata(new GatewayMetadata([], [], [], [], [], [], [], version.Video is null ? [] : [version.Video],
                    CloudVisionAnalyses: version.Photo is null ? [] : [version.Photo]), externalTransaction: transaction, descriptionOnly: true);
            }
            else
            {
                using (var deleted = Command("INSERT OR IGNORE INTO description_delta_tombstones VALUES($epoch,$version)"))
                {
                    deleted.Parameters.AddWithValue("$epoch", page.Cursor.Epoch); deleted.Parameters.AddWithValue("$version", version.VersionId); deleted.ExecuteNonQuery();
                }
                using var remove = Command("DELETE FROM description_versions WHERE epoch=$epoch AND version_id=$version AND kind=$kind AND media_id=$id");
                remove.Parameters.AddWithValue("$epoch", page.Cursor.Epoch); remove.Parameters.AddWithValue("$version", version.VersionId);
                remove.Parameters.AddWithValue("$kind", version.Kind); remove.Parameters.AddWithValue("$id", version.MediaId); remove.ExecuteNonQuery();
                // Remove only the exact deleted projection. Other versions and pending work survive.
                if (version.Kind == "image")
                {
                    foreach (var photo in GetAllMirroredCloudVisionAnalyses(version.MediaId, null))
                        if (CreateDescriptionVersion("image", version.MediaId, null, photo, null).VersionId == version.VersionId)
                        {
                            using var delete = Command("DELETE FROM cloud_vision_analyses WHERE image_id=$id AND provider=$provider");
                            delete.Parameters.AddWithValue("$id", version.MediaId); delete.Parameters.AddWithValue("$provider", photo.Provider); delete.ExecuteNonQuery();
                            RefreshDescriptionSearchText(version.MediaId, transaction);
                        }
                }
                else
                {
                    if (new VideoRepository(this).Get(version.MediaId) is null) continue;
                    var content = new VideoRepository(this).GetContentId(version.MediaId);
                    foreach (var video in GetAllMirroredVideoSummaries(content))
                        if (CreateDescriptionVersion("video", video.VideoId, null, null, video).VersionId == version.VersionId)
                        {
                            using var delete = Command("DELETE FROM video_summaries WHERE video_id=$id");
                            delete.Parameters.AddWithValue("$id", content); delete.ExecuteNonQuery();
                        }
                }
            }
        }
        if (page.Entries.Count > 0 && previous != page.Cursor.Sequence ||
            page.Entries.Count == 0 && page.Cursor.Sequence != page.Through)
            throw new InvalidDataException("description_delta_cursor_invalid");
        if (page.Cursor != expected)
        {
            using var save = Command("INSERT INTO description_delta_checkpoint VALUES(1,$epoch,$scope,$sequence) ON CONFLICT(id) DO UPDATE SET epoch=excluded.epoch,scope=excluded.scope,sequence=excluded.sequence");
            save.Parameters.AddWithValue("$epoch", page.Cursor.Epoch); save.Parameters.AddWithValue("$scope", page.Cursor.Scope);
            save.Parameters.AddWithValue("$sequence", page.Cursor.Sequence); save.ExecuteNonQuery();
        }
        CompleteConfirmedDescriptionUploads();
        transaction.Commit();
    }

    private void CompleteConfirmedDescriptionUploads()
    {
        using var complete = Command("DELETE FROM description_outbox WHERE acknowledged=1 AND EXISTS(SELECT 1 FROM description_delta_seen s WHERE s.epoch=description_outbox.epoch AND s.version_id=description_outbox.version_id)");
        complete.ExecuteNonQuery();
    }

    private bool IsDescriptionTombstoned(string versionId)
    {
        using var query = Command("SELECT 1 FROM description_delta_tombstones WHERE epoch=$epoch AND version_id=$version");
        query.Parameters.AddWithValue("$epoch", DescriptionEpoch()); query.Parameters.AddWithValue("$version", versionId);
        return query.ExecuteScalar() is not null;
    }

    private HashSet<string> ReadDescriptionTombstones()
    {
        using var query = Command("SELECT version_id FROM description_delta_tombstones WHERE epoch=$epoch");
        query.Parameters.AddWithValue("$epoch", DescriptionEpoch());
        using var rows = query.ExecuteReader();
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (rows.Read()) result.Add(rows.GetString(0));
        return result;
    }
}
