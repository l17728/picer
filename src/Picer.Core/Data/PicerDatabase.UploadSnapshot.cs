using System.Globalization;
using Picer.Core.Models;
using Picer.Core.Video;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    internal IEnumerable<ImageRecord> EnumerateUploadImages(long sourceId)
    {
        using var command = Command("SELECT i.* FROM images i JOIN sync_export_media s ON s.kind='image' AND s.media_id=i.id WHERE i.source_id=$source ORDER BY i.id");
        command.Parameters.AddWithValue("$source", sourceId);
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return ReadRecord(reader);
    }

    internal IEnumerable<VideoRecord> EnumerateUploadVideos(long sourceId)
    {
        using var command = Command("""
            SELECT v.id,v.source_id,v.path,v.file_size,v.modified_utc,v.duration_seconds,v.width,v.height,v.taken_utc,v.gps,v.status,
                v.person_analysis_status,v.person_analyzed_utc,v.sha256
            FROM videos v JOIN sync_export_media s ON s.kind='video' AND s.media_id=v.id WHERE v.source_id=$source ORDER BY v.id
            """);
        command.Parameters.AddWithValue("$source", sourceId);
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3),
            DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), reader.IsDBNull(5) ? null : reader.GetDouble(5),
            reader.GetInt32(6), reader.GetInt32(7), reader.IsDBNull(8) ? null : DateTime.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetString(10))
        {
            PersonAnalysisStatus = reader.IsDBNull(11) ? null : reader.GetString(11),
            PersonAnalyzedUtc = reader.IsDBNull(12) ? null : reader.GetString(12), Sha256 = reader.IsDBNull(13) ? null : reader.GetString(13)
        };
    }
}
