using System.Text.Json;
using Picer.Core.Gateway;
using Picer.Core.Video;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    public bool EnableDescriptionSync { get; set; }

    public void RecoverUnsyncedDescriptions()
    {
        if (!EnableDescriptionSync || GetMirrorCheckpoint() is null) return;
        using var transaction = BeginTransaction();
        using var recovered = Command("SELECT 1 FROM description_recovery_state WHERE id=1");
        if (recovered.ExecuteScalar() is not null) return;
        var photos = new List<(long Id, string Provider)>();
        using (var query = Command("""
            SELECT a.image_id,a.provider FROM cloud_vision_analyses a JOIN images i ON i.id=a.image_id
            WHERE a.status='completed' AND EXISTS(SELECT 1 FROM mirror_owned_media m WHERE m.kind='image' AND m.media_id=i.id)
                AND a.source_file_size=i.file_size AND picer_same_instant(a.source_modified_utc,i.modified_utc)
            """))
        using (var rows = query.ExecuteReader())
            while (rows.Read()) photos.Add((rows.GetInt64(0), rows.GetString(1)));
        foreach (var photo in photos)
            QueuePhotoDescription(photo.Id, photo.Provider);
        var videos = new VideoRepository(this);
        foreach (var video in GetAllMirroredVideoSummaries())
            if (DateTimeOffset.TryParse(video.SourceModifiedUtc, out _) &&
                videos.Get(video.VideoId) is { } current && videos.HasCurrentSummary(current)) QueueVideoDescription(video.VideoId);
        using var complete = Command("INSERT INTO description_recovery_state VALUES(1)");
        complete.ExecuteNonQuery();
        transaction.Commit();
    }

    // Called inside the same transaction as the completed result, never after it.
    public void QueuePhotoDescription(long imageId, string provider)
    {
        ArchiveCurrentPhotoDescription(imageId, provider);
        if (!EnableDescriptionSync) return;
        var photo = GetAllMirroredCloudVisionAnalyses(imageId, provider).Single();
        var image = GetImage(imageId) ?? throw new InvalidOperationException("description_media_missing");
        QueueDescription(new(Guid.NewGuid().ToString("N"), RequireDescriptionEpoch(), "image", imageId,
            image.ContentSha256 ?? image.Sha256, photo, null), provider);
    }

    public void QueueVideoDescription(long videoId)
    {
        ArchiveCurrentVideoDescription(videoId);
        if (!EnableDescriptionSync) return;
        var repository = new VideoRepository(this);
        var content = repository.GetContentId(videoId);
        var summary = GetAllMirroredVideoSummaries(content).Single();
        var video = repository.Get(summary.VideoId) ?? throw new InvalidOperationException("description_media_missing");
        QueueDescription(new(Guid.NewGuid().ToString("N"), RequireDescriptionEpoch(), "video", summary.VideoId,
            video.Sha256, null, summary), summary.Provider);
    }

    private string RequireDescriptionEpoch() => GetMirrorCheckpoint()?.Epoch
        ?? throw new InvalidOperationException("description_mirror_baseline_required");

    private void QueueDescription(DescriptionUpload item, string provider)
    {
        var version = CreateDescriptionVersion(item.Kind, item.MediaId, item.Sha256, item.Photo, item.Video);
        StoreDescriptionVersion(version);
        using var command = Command("""
            INSERT OR IGNORE INTO description_outbox(kind,media_id,provider,operation_id,version_id,epoch,payload)
            VALUES($kind,$id,$provider,$op,$version,$epoch,$payload)
            """);
        command.Parameters.AddWithValue("$version", version.VersionId);
        command.Parameters.AddWithValue("$epoch", item.Epoch);
        command.Parameters.AddWithValue("$kind", item.Kind);
        command.Parameters.AddWithValue("$id", item.MediaId);
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$op", item.OperationId);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(item));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<DescriptionUpload> GetDescriptionOutbox(bool pendingOnly = false,
        IReadOnlySet<long>? imageIds = null, IReadOnlySet<long>? videoIds = null)
    {
        using var command = Command("SELECT payload FROM description_outbox WHERE ($pending=0 OR acknowledged=0) AND ($images IS NULL OR kind='image' AND media_id IN (SELECT value FROM json_each($images)) OR kind='video' AND media_id IN (SELECT value FROM json_each($videos))) ORDER BY rowid");
        command.Parameters.AddWithValue("$pending", pendingOnly ? 1 : 0);
        command.Parameters.AddWithValue("$images", imageIds is null ? DBNull.Value : JsonSerializer.Serialize(imageIds));
        command.Parameters.AddWithValue("$videos", videoIds is null ? DBNull.Value : JsonSerializer.Serialize(videoIds));
        using var rows = command.ExecuteReader();
        var result = new List<DescriptionUpload>();
        while (rows.Read()) result.Add(JsonSerializer.Deserialize<DescriptionUpload>(rows.GetString(0))!);
        return result;
    }

    public void AcknowledgeDescription(string operationId)
    {
        using var command = Command("UPDATE description_outbox SET acknowledged=1 WHERE operation_id=$op");
        command.Parameters.AddWithValue("$op", operationId);
        command.ExecuteNonQuery();
        CompleteConfirmedDescriptionUploads();
    }

    public long DescriptionOutboxHighWater()
    {
        using var command = Command("SELECT COALESCE(MAX(sequence),0) FROM description_outbox_order");
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public IReadOnlyList<(long Sequence, DescriptionUpload Item)> ReadDescriptionOutboxPage(long after, long through)
    {
        using var command = Command("SELECT o.sequence,d.payload FROM description_outbox_order o JOIN description_outbox d ON d.operation_id=o.operation_id WHERE d.acknowledged=0 AND o.sequence>$after AND o.sequence<=$through ORDER BY o.sequence LIMIT 20");
        command.Parameters.AddWithValue("$after", after); command.Parameters.AddWithValue("$through", through);
        using var rows = command.ExecuteReader();
        var result = new List<(long, DescriptionUpload)>();
        while (rows.Read()) result.Add((rows.GetInt64(0), JsonSerializer.Deserialize<DescriptionUpload>(rows.GetString(1))!));
        return result;
    }

    public (long Pending, long AwaitingSnapshot) GetDescriptionSyncCounts()
    {
        using var command = Command("SELECT COALESCE(SUM(acknowledged=0),0),COALESCE(SUM(acknowledged=1),0) FROM description_outbox");
        using var row = command.ExecuteReader(); row.Read();
        return (row.GetInt64(0), row.GetInt64(1));
    }

    // A response to POST is not enough: an older GET may still be in flight.
    // Keep the local result until the authoritative snapshot contains the exact receipt's result.
    public GatewayMetadata ProtectDescriptionSnapshot(GatewayMetadata metadata, bool descriptionOnly = false)
    {
        ArchiveDescriptionSnapshot(metadata);
        var tombstones = ReadDescriptionTombstones();
        var photos = metadata.CloudVisionAnalysesOrEmpty.Where(photo =>
            !tombstones.Contains(CreateDescriptionVersion("image", photo.ImageId, null, photo, null).VersionId)).ToList();
        var videos = metadata.VideoSummaries.Select(CanonicalDescriptionVideo).Where(video =>
            !tombstones.Contains(CreateDescriptionVersion("video", video.VideoId, null, null, video).VersionId)).ToList();
        var photoIds = descriptionOnly ? metadata.CloudVisionAnalysesOrEmpty.Select(x => x.ImageId).ToHashSet() : null;
        var videoIds = descriptionOnly ? metadata.VideoSummaries.Select(x => x.VideoId).ToHashSet() : null;
        foreach (var item in GetDescriptionOutbox(imageIds: photoIds, videoIds: videoIds))
        {
            if (item.Epoch != GetMirrorCheckpoint()?.Epoch) continue;
            var versionId = CreateDescriptionVersion(item.Kind, item.MediaId, item.Sha256, item.Photo, item.Video).VersionId;
            if (tombstones.Contains(versionId)) continue;
            var matches = (metadata.DescriptionVersions?.Any(x => x.VersionId == versionId) ?? false) || (item.Photo is { } photo
                ? photos.Any(x => x == photo)
                : item.Video is { } video && videos.Any(x => x == video));
            if (matches)
            {
                using var remove = Command("DELETE FROM description_outbox WHERE operation_id=$op AND acknowledged=1");
                remove.Parameters.AddWithValue("$op", item.OperationId);
                remove.ExecuteNonQuery();
            }
            if (item.Photo is { } localPhoto && GetImage(item.MediaId) is { } image &&
                image.FileSize == localPhoto.SourceFileSize && SourceTimestamp.SameInstant(image.ModifiedUtc.ToString("o"), localPhoto.SourceModifiedUtc) &&
                (item.Sha256 is null || item.Sha256.Equals(image.ContentSha256 ?? image.Sha256, StringComparison.OrdinalIgnoreCase)))
            {
                photos.Add(localPhoto);
            }
            if (item.Video is { } localVideo && new VideoRepository(this).Get(item.MediaId) is { } currentVideo &&
                currentVideo.FileSize == localVideo.SourceFileSize &&
                (item.Sha256 is { Length: 64 } ? item.Sha256.Equals(currentVideo.Sha256, StringComparison.OrdinalIgnoreCase)
                    : SourceTimestamp.SameInstant(currentVideo.ModifiedUtc.ToString("o"), localVideo.SourceModifiedUtc)))
            {
                videos.Add(localVideo);
            }
        }
        // Omission is not deletion, including snapshots from legacy peers.
        // Explicit media purge removes owned history through delete triggers.
        foreach (var version in GetDescriptionVersions(photoIds, videoIds))
        {
            if (version.Photo is { } photo) photos.Add(photo);
            if (version.Video is { } video) videos.Add(video);
        }
        photos = photos.GroupBy(x => (x.ImageId, x.Provider)).Select(group => group
            .Select(x => CreateDescriptionVersion("image", x.ImageId, null, x, null))
            .OrderByDescending(DescriptionTime).ThenByDescending(x => x.VersionId, StringComparer.Ordinal).First().Photo!).ToList();
        var repository = new VideoRepository(this);
        videos = videos.GroupBy(x => repository.GetContentId(x.VideoId)).Select(group => group
            .Select(x => CreateDescriptionVersion("video", x.VideoId, null, null, x))
            .OrderByDescending(DescriptionTime).ThenByDescending(x => x.VersionId, StringComparer.Ordinal).First().Video!).ToList();
        return metadata with { CloudVisionAnalyses = metadata.CloudVisionAnalyses is null && photos.Count == 0 ? null : photos,
            VideoSummaries = videos };
    }
}
