using Picer.Core.Models;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    /// <summary>Keyset pagination follows the existing directory expression index; no OFFSET or full-library sort.</summary>
    public IReadOnlyList<DirectoryDescriptionItem> ReadDescriptionPage(string directory, bool video, string afterPath,
        long afterId, long throughId, int pageSize = 32, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(directory) || pageSize is < 1 or > 128) throw new ArgumentException("invalid_description_page");
        token.ThrowIfCancellationRequested();
        var table = video ? "videos" : "images";
        var prefix = Picer.Core.Indexing.MediaSourcePath.Normalize(directory) + "/";
        // The prefix ends with '/', whose immediate ordinal successor is '0'.
        // Use an explicit seek bound: SQLite's LIKE optimization otherwise keeps
        // seeking from the directory start, even with a later tuple cursor.
        var upper = prefix[..^1] + "0";
        var seek = afterPath.Length == 0 ? prefix : afterPath;
        using var command = Command($"""
            SELECT * FROM {table} INDEXED BY idx_{table}_directory_path
            WHERE status='active' AND replace(path,char(92),'/') COLLATE NOCASE >= $seek
              AND replace(path,char(92),'/') COLLATE NOCASE < $upper
              AND (replace(path,char(92),'/') COLLATE NOCASE,id)>($after,$id) AND id<=$through
            ORDER BY replace(path,char(92),'/') COLLATE NOCASE,id LIMIT $limit
            """);
        command.Parameters.AddWithValue("$seek", seek); command.Parameters.AddWithValue("$upper", upper);
        command.Parameters.AddWithValue("$after", afterPath); command.Parameters.AddWithValue("$id", afterId);
        command.Parameters.AddWithValue("$through", throughId); command.Parameters.AddWithValue("$limit", pageSize);
        var images = new List<ImageRecord>(); var videos = new List<VideoRecord>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (!video) { images.Add(ReadRecord(reader)); continue; }
                string? Text(string name) { var ordinal = reader.GetOrdinal(name); return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal); }
                long Number(string name) => reader.GetInt64(reader.GetOrdinal(name));
                videos.Add(new(Number("id"), Number("source_id"), Text("path")!, Number("file_size"), Parse(Text("modified_utc")!),
                    reader.IsDBNull(reader.GetOrdinal("duration_seconds")) ? null : reader.GetDouble(reader.GetOrdinal("duration_seconds")),
                    (int)Number("width"), (int)Number("height"), Text("taken_utc") is { } taken ? Parse(taken) : null,
                    Text("gps"), Text("status")!) { Sha256 = Text("sha256"), PersonAnalysisStatus = Text("person_analysis_status"), PersonAnalyzedUtc = Text("person_analyzed_utc") });
            }
        if (!video)
        {
            var described = new CloudVisionRepository(this).GetCurrentDescriptionIds(images, token);
            return images.Select(image => new DirectoryDescriptionItem(image, null, described.Contains(image.Id))).ToArray();
        }
        var repository = new VideoRepository(this);
        var result = new List<DirectoryDescriptionItem>();
        foreach (var item in videos)
        {
            token.ThrowIfCancellationRequested();
            result.Add(new(null, item, repository.HasCurrentSummary(item)));
        }
        return result;
    }

    public long GetDescriptionHighWatermark(bool video)
    {
        using var command = Command(video ? "SELECT COALESCE(MAX(id),0) FROM videos" : "SELECT COALESCE(MAX(id),0) FROM images");
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
