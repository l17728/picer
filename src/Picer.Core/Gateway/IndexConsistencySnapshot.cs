using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Video;

namespace Picer.Core.Gateway;

/// <summary>Immutable evidence captured at an index authority switch boundary.</summary>
public sealed record IndexConsistencySnapshot(
    string Epoch,
    long MediaRevision,
    long MetadataRevision,
    int ImageCount,
    int VideoCount,
    string MediaDigest,
    string MetadataDigest,
    long PendingMetadataEdits,
    long PendingDescriptions)
{
    public bool Matches(IndexConsistencySnapshot other, bool requireNoPendingWrites = true)
    {
        if (!string.Equals(Epoch, other.Epoch, StringComparison.Ordinal) ||
            ImageCount != other.ImageCount || VideoCount != other.VideoCount ||
            !string.Equals(MediaDigest, other.MediaDigest, StringComparison.Ordinal) ||
            !string.Equals(MetadataDigest, other.MetadataDigest, StringComparison.Ordinal)) return false;
        return !requireNoPendingWrites ||
            (PendingMetadataEdits == 0 && PendingDescriptions == 0 &&
             other.PendingMetadataEdits == 0 && other.PendingDescriptions == 0);
    }
}

public static class IndexConsistencySnapshotBuilder
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IndexConsistencySnapshot CaptureLocal(PicerDatabase database)
    {
        var checkpoint = database.GetMirrorCheckpoint() ?? new MirrorCheckpoint("local", 0, 0);
        var images = database.GetImages(includeMissing: true);
        var videos = new VideoRepository(database).GetAll();
        var metadata = IndexSyncClient.BuildMetadataSnapshot(database);
        var pendingMetadata = database.GetPendingMetadataEdits().Count;
        var pendingDescriptions = database.GetDescriptionOutbox(pendingOnly: true).Count;
        return new(checkpoint.Epoch, checkpoint.MediaRevision, checkpoint.MetadataRevision,
            images.Count, videos.Count, Digest(images.Select(MediaKey).Concat(videos.Select(MediaKey))),
            DigestMetadata(metadata), pendingMetadata, pendingDescriptions);
    }

    public static async Task<IndexConsistencySnapshot> CaptureRemoteAsync(
        IIndexGateway gateway, CancellationToken cancellationToken = default)
    {
        var revision = await gateway.GetChangesAsync(new GatewayChangeRequest(After: 0, Through: null, PageSize: 1), cancellationToken).ConfigureAwait(false);
        var sources = await gateway.ListSourcesAsync(cancellationToken).ConfigureAwait(false);
        var images = new List<ImageRecord>();
        var videos = new List<VideoRecord>();
        foreach (var source in sources)
        {
            string? cursor = null;
            do
            {
                var page = await gateway.ListImagesAsync(source.Id, includeMissing: true, pageSize: 500, cursor, cancellationToken).ConfigureAwait(false);
                images.AddRange(page.Items);
                cursor = page.NextCursor;
            } while (cursor is not null);
            cursor = null;
            do
            {
                var page = await gateway.ListVideosAsync(source.Id, pageSize: 500, cursor, cancellationToken).ConfigureAwait(false);
                videos.AddRange(page.Items);
                cursor = page.NextCursor;
            } while (cursor is not null);
        }
        var metadata = await gateway.GetMetadataAsync(cancellationToken).ConfigureAwait(false);
        return new(revision.Epoch, revision.Through, revision.MetadataRevision,
            images.Count, videos.Count, Digest(images.Select(MediaKey).Concat(videos.Select(MediaKey))),
            DigestMetadata(metadata), 0, 0);
    }

    /// <summary>Read-only probes used as the final gate before changing authority.</summary>
    public static async Task ProbeReadPathAsync(IIndexGateway gateway, CancellationToken cancellationToken = default)
    {
        await gateway.ListDirectoriesAsync(cancellationToken).ConfigureAwait(false);
        await gateway.ListTagsAsync(cancellationToken).ConfigureAwait(false);
        await gateway.ListAlbumsAsync(cancellationToken).ConfigureAwait(false);
        var page = await gateway.SearchAsync(new SearchFilters { IncludeMissing = true }, 1, null, cancellationToken).ConfigureAwait(false);
        if (page.Items.Count == 0) return;
        await using var thumbnail = await gateway.OpenThumbnailAsync(page.Items[0].Id, cancellationToken).ConfigureAwait(false);
        if (thumbnail is null) throw new InvalidDataException("index_mode_switch_thumbnail_probe_failed");
        var buffer = new byte[1];
        if (await thumbnail.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidDataException("index_mode_switch_thumbnail_probe_empty");
    }

    private static string MediaKey(ImageRecord image) =>
        $"image|{image.Id}|{image.Path}|{image.FileSize}|{image.ModifiedUtc:o}|{image.Sha256 ?? image.ContentSha256}";
    private static string MediaKey(VideoRecord video) =>
        $"video|{video.Id}|{video.Path}|{video.FileSize}|{video.ModifiedUtc:o}|{video.Sha256}";

    private static string Digest(IEnumerable<string> values)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', values.OrderBy(x => x, StringComparer.Ordinal)));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string DigestMetadata(GatewayMetadata metadata)
    {
        IEnumerable<string> Values<T>(IEnumerable<T> value) =>
            value.Select(item => JsonSerializer.Serialize(item, Json));
        var values = Values(metadata.AlbumImages)
            .Concat(Values(metadata.ImageTags)).Concat(Values(metadata.Locations))
            .Concat(Values(metadata.Faces)).Concat(Values(metadata.FaceAssignments))
            .Concat(Values(metadata.VideoKeyframes)).Concat(Values(metadata.VideoTranscripts))
            .Concat(Values(metadata.VideoSummaries)).Concat(Values(metadata.VideoFacesOrEmpty))
            .Concat(Values(metadata.CloudVisionAnalysesOrEmpty)).Concat(Values(metadata.SceneGroupDescriptionsOrEmpty))
            .Concat(Values(metadata.FaceReviewStatesOrEmpty)).Concat(Values(metadata.FaceCandidateClustersOrEmpty))
            .Concat(Values(metadata.FaceCandidateMembersOrEmpty)).Concat(Values(metadata.FaceTrainingArtifactsOrEmpty))
            .Concat(Values(metadata.FacePersonPrototypesOrEmpty)).Concat(Values(metadata.FacePersonPoliciesOrEmpty))
            .Concat(Values(metadata.FaceModelSettingsOrEmpty));
        return Digest(values);
    }
}
