using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Picer.Core.Gateway;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    public DescriptionUploadReceipt AcceptDescriptionUpload(DescriptionUpload item)
    {
        if (!Guid.TryParseExact(item.OperationId, "N", out _) || item.MediaId <= 0 ||
            item.Kind is not ("image" or "video") || (item.Photo is null) == (item.Video is null))
            throw new ArgumentException("invalid_description");
        using var transaction = BeginTransaction();
        using (var state = Command("SELECT epoch FROM mirror_revision WHERE id=1 AND EXISTS(SELECT 1 FROM sync_space_state WHERE id=1 AND state='active')"))
            if (state.ExecuteScalar() as string != item.Epoch) throw new InvalidOperationException("description_baseline_changed");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item))));
        using (var receipt = Command("SELECT payload_hash FROM description_receipts WHERE operation_id=$op"))
        {
            receipt.Parameters.AddWithValue("$op", item.OperationId);
            if (receipt.ExecuteScalar() is string previous)
            {
                if (previous != hash) throw new InvalidOperationException("description_operation_conflict");
                return new(item.OperationId);
            }
        }
        var photo = item.Photo;
        var video = item.Video;
        if (photo is not null && (item.Kind != "image" || photo.ImageId != item.MediaId || photo.Status != "completed") ||
            video is not null && (item.Kind != "video" || video.VideoId != item.MediaId)) throw new ArgumentException("invalid_description");
        var provider = photo is not null ? photo.Provider : video!.Provider;
        var model = photo is not null ? photo.ModelVersion : video!.ModelVersion;
        var prompt = photo is not null ? photo.PromptVersion : video!.PromptVersion;
        var summary = photo is not null ? photo.Summary : video!.Summary;
        var json = photo?.ResultJson ?? video?.ResultJson;
        var updated = photo?.UpdatedUtc ?? video?.UpdatedUtc;
        var size = photo?.SourceFileSize ?? video?.SourceFileSize;
        var modified = photo?.SourceModifiedUtc ?? video?.SourceModifiedUtc;
        if (string.IsNullOrWhiteSpace(provider) || provider.Length > 256 || string.IsNullOrWhiteSpace(model) || model.Length > 512 ||
            string.IsNullOrWhiteSpace(prompt) || prompt.Length > 256 || string.IsNullOrWhiteSpace(summary) || summary.Length > 65536 ||
            json is null || json.Length > 1024 * 1024 || size is null or < 0 ||
            !DateTimeOffset.TryParse(updated, out _) || !DateTimeOffset.TryParse(modified, out _)) throw new ArgumentException("invalid_description");
        var parsed = JsonSerializer.Deserialize<CloudVisionResult>(json) ?? throw new ArgumentException("invalid_description");
        if (parsed.Summary != summary) throw new ArgumentException("description_summary_mismatch");
        string? actualHash;
        long actualSize;
        DateTime actualModified;
        if (photo is not null)
        {
            var image = GetImage(item.MediaId) ?? throw new InvalidOperationException("description_media_changed");
            actualHash = image.ContentSha256 ?? image.Sha256; actualSize = image.FileSize; actualModified = image.ModifiedUtc;
        }
        else
        {
            var record = new VideoRepository(this).Get(item.MediaId) ?? throw new InvalidOperationException("description_media_changed");
            actualHash = record.Sha256; actualSize = record.FileSize; actualModified = record.ModifiedUtc;
        }
        if (actualSize != size || (photo is not null || item.Sha256 is null) && actualModified.ToUniversalTime() != DateTimeOffset.Parse(modified!).UtcDateTime ||
            item.Sha256 is { } supplied && (!supplied.Equals(actualHash, StringComparison.OrdinalIgnoreCase) || supplied.Length != 64))
            throw new InvalidOperationException("description_media_changed");
        var currentPhoto = photo is null ? null : GetAllMirroredCloudVisionAnalyses(item.MediaId, provider).SingleOrDefault();
        var currentVideo = video is null ? null : GetAllMirroredVideoSummaries(new VideoRepository(this).GetContentId(item.MediaId)).SingleOrDefault();
        ArchiveDescriptionSnapshot(new GatewayMetadata([], [], [], [], [], [], [], currentVideo is null ? [] : [currentVideo],
            CloudVisionAnalyses: currentPhoto is null ? [] : [currentPhoto]));
        // Keep both sides of a conflict. The active projection is deterministic, while
        // every accepted version is returned in subsequent metadata snapshots.
        UpsertMirroredVideoMetadata(new GatewayMetadata([], [], [], [], [], [], [],
            new[] { currentVideo, video }.OfType<GatewayVideoSummary>().ToArray(),
            CloudVisionAnalyses: new[] { currentPhoto, photo }.OfType<GatewayCloudVisionAnalysis>().ToArray()), externalTransaction: transaction);
        using (var receipt = Command("INSERT INTO description_receipts(operation_id,payload_hash) VALUES($op,$hash)"))
        {
            receipt.Parameters.AddWithValue("$op", item.OperationId); receipt.Parameters.AddWithValue("$hash", hash); receipt.ExecuteNonQuery();
        }
        transaction.Commit();
        return new(item.OperationId);
    }
}
