using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Picer.Core.Gateway;
using Picer.Core.Video;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    private string DescriptionEpoch()
    {
        if (GetMirrorCheckpoint() is { } checkpoint) return checkpoint.Epoch;
        using var command = Command("SELECT epoch FROM mirror_revision WHERE id=1");
        return (string)command.ExecuteScalar()!;
    }

    public static DescriptionVersion CreateDescriptionVersion(string kind, long mediaId, string? sha256,
        GatewayCloudVisionAnalysis? photo, GatewayVideoSummary? video)
    {
        // Delivery operation IDs and arrival order must not change a version's identity.
        var canonical = JsonSerializer.Serialize(new { Kind = kind, MediaId = mediaId, Photo = photo, Video = video });
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new(id, kind, mediaId, sha256, photo, video);
    }

    private bool IsCurrentDescriptionVersion(DescriptionVersion version)
    {
        var size = version.Photo?.SourceFileSize ?? version.Video?.SourceFileSize;
        var modified = version.Photo?.SourceModifiedUtc ?? version.Video?.SourceModifiedUtc;
        if (!DateTimeOffset.TryParse(modified, out var timestamp)) return false;
        if (version.Kind == "image" && version.Photo is { Status: "completed" } photo && version.Video is null && photo.ImageId == version.MediaId)
        {
            var image = GetImage(version.MediaId);
            return image is not null && image.FileSize == size && image.ModifiedUtc.ToUniversalTime() == timestamp.UtcDateTime &&
                (version.Sha256 is null || version.Sha256.Equals(image.ContentSha256 ?? image.Sha256, StringComparison.OrdinalIgnoreCase));
        }
        if (version.Kind == "video" && version.Video is { } video && version.Photo is null && video.VideoId == version.MediaId)
        {
            var current = new VideoRepository(this).Get(version.MediaId);
            return current is not null && current.FileSize == size &&
                (version.Sha256 is { Length: 64 } ? version.Sha256.Equals(current.Sha256, StringComparison.OrdinalIgnoreCase)
                    : current.ModifiedUtc.ToUniversalTime() == timestamp.UtcDateTime);
        }
        return false;
    }

    private void StoreDescriptionVersion(DescriptionVersion version)
    {
        if (CreateDescriptionVersion(version.Kind, version.MediaId, version.Sha256, version.Photo, version.Video).VersionId != version.VersionId)
            throw new InvalidDataException("description_version_hash_mismatch");
        if (!IsCurrentDescriptionVersion(version)) return;
        if (version.Video is { } video)
        {
            var canonical = CanonicalDescriptionVideo(video);
            version = CreateDescriptionVersion("video", canonical.VideoId, version.Sha256, null, canonical);
        }
        using var command = Command("INSERT OR IGNORE INTO description_versions VALUES($version,$kind,$id,$epoch,$payload)");
        command.Parameters.AddWithValue("$version", version.VersionId);
        command.Parameters.AddWithValue("$kind", version.Kind);
        command.Parameters.AddWithValue("$id", version.MediaId);
        command.Parameters.AddWithValue("$epoch", DescriptionEpoch());
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(version));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<DescriptionVersion> GetDescriptionVersions()
    {
        var versions = new List<DescriptionVersion>();
        using (var command = Command("SELECT payload FROM description_versions WHERE epoch=$epoch ORDER BY version_id"))
        {
            command.Parameters.AddWithValue("$epoch", DescriptionEpoch());
            using var rows = command.ExecuteReader();
            while (rows.Read()) versions.Add(JsonSerializer.Deserialize<DescriptionVersion>(rows.GetString(0))!);
        }
        // Databases upgraded from v91 already persist active descriptions, but
        // have no history rows until the next write. Include those current rows
        // in the read contract so a new client does not invent a local-only
        // version merely by receiving an older server's persisted description.
        foreach (var photo in GetAllMirroredCloudVisionAnalyses())
        {
            var image = GetImage(photo.ImageId);
            versions.Add(CreateDescriptionVersion("image", photo.ImageId, image?.ContentSha256 ?? image?.Sha256, photo, null));
        }
        var repository = new VideoRepository(this);
        foreach (var video in GetAllMirroredVideoSummaries())
            versions.Add(CreateDescriptionVersion("video", video.VideoId, repository.Get(video.VideoId)?.Sha256, null, video));
        return versions.Where(IsCurrentDescriptionVersion).DistinctBy(version => version.VersionId)
            .OrderBy(version => version.VersionId, StringComparer.Ordinal).ToArray();
    }

    private void ArchiveDescriptionSnapshot(GatewayMetadata metadata)
    {
        foreach (var version in metadata.DescriptionVersions ?? []) StoreDescriptionVersion(version);
        foreach (var photo in metadata.CloudVisionAnalysesOrEmpty)
        {
            var image = GetImage(photo.ImageId);
            foreach (var current in GetAllMirroredCloudVisionAnalyses(photo.ImageId, photo.Provider))
                StoreDescriptionVersion(CreateDescriptionVersion("image", photo.ImageId, image?.ContentSha256 ?? image?.Sha256, current, null));
            StoreDescriptionVersion(CreateDescriptionVersion("image", photo.ImageId, image?.ContentSha256 ?? image?.Sha256, photo, null));
        }
        foreach (var video in metadata.VideoSummaries)
        {
            ArchiveCurrentVideoDescription(video.VideoId);
            StoreDescriptionVersion(CreateDescriptionVersion("video", video.VideoId, new VideoRepository(this).Get(video.VideoId)?.Sha256, null, video));
        }
    }

    public void ArchiveCurrentPhotoDescription(long imageId, string provider)
    {
        var image = GetImage(imageId);
        foreach (var current in GetAllMirroredCloudVisionAnalyses(imageId, provider))
            StoreDescriptionVersion(CreateDescriptionVersion("image", imageId, image?.ContentSha256 ?? image?.Sha256, current, null));
    }

    public void ArchiveCurrentVideoDescription(long videoId)
    {
        var repository = new VideoRepository(this);
        foreach (var current in GetAllMirroredVideoSummaries(repository.GetContentId(videoId)))
            StoreDescriptionVersion(CreateDescriptionVersion("video", current.VideoId, repository.Get(current.VideoId)?.Sha256, null, current));
    }

    public void QueueRecoveredDescription(GatewayCloudVisionAnalysis? photo, GatewayVideoSummary? video)
    {
        if (video is not null) video = CanonicalDescriptionVideo(video);
        var id = photo?.ImageId ?? video?.VideoId ?? throw new ArgumentException("missing_description");
        var hash = photo is not null ? GetImage(id)?.ContentSha256 ?? GetImage(id)?.Sha256 : new VideoRepository(this).Get(id)?.Sha256;
        var version = CreateDescriptionVersion(photo is null ? "video" : "image", id, hash, photo, video);
        if (!IsCurrentDescriptionVersion(version)) throw new InvalidOperationException("description_media_changed");
        QueueDescription(new(Guid.NewGuid().ToString("N"), RequireDescriptionEpoch(), version.Kind, id, hash, photo, video),
            photo?.Provider ?? video!.Provider);
    }

    private static DateTimeOffset DescriptionTime(DescriptionVersion version) =>
        DateTimeOffset.TryParse(version.Photo?.UpdatedUtc ?? version.Video?.UpdatedUtc, out var time) ? time : DateTimeOffset.MinValue;

    private GatewayVideoSummary CanonicalDescriptionVideo(GatewayVideoSummary video)
    {
        var contentId = new VideoRepository(this).GetContentId(video.VideoId);
        using var query = Command("SELECT MIN(id) FROM videos WHERE content_id=$content");
        query.Parameters.AddWithValue("$content", contentId);
        return query.ExecuteScalar() is long id ? video with { VideoId = id } : video;
    }

    private void RefreshDescriptionSearchText(long imageId, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        var image = GetImage(imageId);
        var description = new Picer.Core.Vision.CloudVisionRepository(this).GetLatestCompleted(imageId);
        if (image is null || description is null) return;
        var active = description.Value;
        var result = active.Result;
        new Picer.Core.Search.MediaTextIndexRepository(this).UpsertImage(image,
            [image.Path, image.Camera, image.Gps, image.TakenUtc?.ToString("O"), result.Summary,
                .. result.Scenes ?? [], .. result.Activities ?? [], .. result.Objects ?? [], .. result.SearchTerms ?? []],
            $"cloud:{active.Provider}:{active.ModelVersion}:{active.PromptVersion}", transaction);
    }
}
