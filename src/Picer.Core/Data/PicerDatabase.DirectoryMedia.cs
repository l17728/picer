using Picer.Core.Indexing;
using Picer.Core.Models;
using Picer.Core.Video;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    private static string DirectoryLikePattern(string directory) =>
        (DirectoryMediaImport.Normalize(directory) + "/").Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "%";

    public IReadOnlyList<ImageRecord> GetImagesInDirectory(string directory, long? sourceId = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var command = Command("SELECT * FROM images WHERE status='active' AND ($source IS NULL OR source_id=$source) AND replace(path,char(92),'/') LIKE $prefix ESCAPE '!' ORDER BY path COLLATE NOCASE");
        command.Parameters.AddWithValue("$source", (object?)sourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$prefix", DirectoryLikePattern(directory));
        using var reader = command.ExecuteReader(); var result = new List<ImageRecord>();
        while (reader.Read()) { token.ThrowIfCancellationRequested(); result.Add(ReadRecord(reader)); }
        return result;
    }

    public IReadOnlyList<string> GetDirectoryTreePaths(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        _conn.CreateFunction<string, string>("directory_node", path =>
        {
            var normalized = DirectoryMediaImport.Normalize(path);
            var slash = normalized.LastIndexOf('/');
            return slash < 0 ? normalized + "/.directory-node" : normalized[..slash] + "/.directory-node";
        }, isDeterministic: true);
        using var command = Command("SELECT DISTINCT directory_node(path) FROM images WHERE status='active' UNION SELECT DISTINCT directory_node(path) FROM videos WHERE status='active' UNION SELECT directory_node(rtrim(path,'/\\') || '/.directory-node') FROM sources");
        using var reader = command.ExecuteReader(); var result = new List<string>();
        while (reader.Read()) { token.ThrowIfCancellationRequested(); result.Add(reader.GetString(0)); }
        return result;
    }

    public IReadOnlyList<(VideoRecord Video, bool Described)> GetVideosInDirectory(string directory, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var command = Command("""
            SELECT v.id,v.source_id,v.path,v.file_size,v.modified_utc,v.duration_seconds,v.width,v.height,v.taken_utc,v.gps,v.status,
                v.sha256,v.person_analysis_status,v.person_analyzed_utc,
                EXISTS(SELECT 1 FROM video_summaries s WHERE s.video_id=v.content_id AND length(trim(s.summary))>0
                    AND s.source_file_size=v.file_size AND (s.source_modified_utc=v.modified_utc OR length(v.sha256)=64))
            FROM videos v WHERE v.status='active' AND replace(v.path,char(92),'/') LIKE $prefix ESCAPE '!' ORDER BY v.path COLLATE NOCASE
            """);
        command.Parameters.AddWithValue("$prefix", DirectoryLikePattern(directory));
        using var reader = command.ExecuteReader(); var result = new List<(VideoRecord, bool)>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            result.Add((new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3), Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetDouble(5), reader.GetInt32(6), reader.GetInt32(7), reader.IsDBNull(8) ? null : Parse(reader.GetString(8)),
                reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetString(10))
                { Sha256 = reader.IsDBNull(11) ? null : reader.GetString(11), PersonAnalysisStatus = reader.IsDBNull(12) ? null : reader.GetString(12), PersonAnalyzedUtc = reader.IsDBNull(13) ? null : reader.GetString(13) }, reader.GetBoolean(14)));
        }
        return result;
    }
}
