using System.Text.Json;
using Picer.Core.Models;

namespace Picer.Core.Vision;

public sealed partial class CloudVisionRepository
{
    public string? GetFailureDetail(long imageId, string provider)
    {
        using var command = database.Command("SELECT error_code FROM cloud_vision_analyses WHERE image_id=$id AND provider=$provider AND status='failed'");
        command.Parameters.AddWithValue("$id", imageId); command.Parameters.AddWithValue("$provider", provider);
        return command.ExecuteScalar() as string;
    }
    /// <summary>Read-only batch preview, including reusable content evidence without writing projections.</summary>
    public IReadOnlySet<long> GetCurrentDescriptionIds(IReadOnlyList<ImageRecord> images, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var result = new HashSet<long>();
        foreach (var batch in images.Chunk(500))
        {
            token.ThrowIfCancellationRequested();
            using var command = database.Command("""
                SELECT i.id FROM images i JOIN json_each($ids) requested ON requested.value=i.id
                WHERE EXISTS(SELECT 1 FROM cloud_vision_analyses c WHERE c.image_id=i.id AND c.status='completed'
                    AND length(trim(c.summary))>0 AND c.source_file_size=i.file_size AND c.source_modified_utc=i.modified_utc)
                OR EXISTS(SELECT 1 FROM content_derivatives d WHERE d.content_sha256=COALESCE(i.content_sha256,i.sha256)
                    AND d.kind='cloud_vision_analyses' AND json_extract(d.payload,'$.status')='completed'
                    AND length(trim(json_extract(d.payload,'$.summary')))>0
                    AND json_extract(d.payload,'$.source_file_size')=i.file_size
                    AND length(json_extract(d.payload,'$.source_modified_utc'))>0)
                """);
            command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(batch.Select(image => image.Id)));
            using var reader = command.ExecuteReader();
            while (reader.Read()) { token.ThrowIfCancellationRequested(); result.Add(reader.GetInt64(0)); }
        }
        return result;
    }
}
