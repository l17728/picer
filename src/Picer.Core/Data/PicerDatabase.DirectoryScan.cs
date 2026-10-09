using Microsoft.Data.Sqlite;
using Picer.Core.Indexing;
using Picer.Core.Models;
using Picer.Core.Video;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    public sealed record DirectoryScanCandidate(long Id, string Path, long Size, DateTime Modified, bool Video);

    public IReadOnlyList<DirectoryScanCandidate> GetDirectoryScanCandidates(long sourceId, string directory)
    {
        using var command = Command("""
            SELECT id,path,file_size,modified_utc,0 AS video FROM images WHERE source_id=$source AND status<>'missing'
            UNION ALL
            SELECT id,path,file_size,modified_utc,1 AS video FROM videos WHERE source_id=$source AND status<>'missing'
            """);
        command.Parameters.AddWithValue("$source", sourceId);
        using var reader = command.ExecuteReader();
        var result = new List<DirectoryScanCandidate>();
        while (reader.Read())
        {
            var path = reader.GetString(1);
            if (DirectoryMediaImport.Contains(directory, path))
                result.Add(new(reader.GetInt64(0), path, reader.GetInt64(2), Parse(reader.GetString(3)), reader.GetInt32(4) != 0));
        }
        return result;
    }

    public IReadOnlyList<ImageRecord> GetScanImagesByPaths(long sourceId, IEnumerable<string> paths)
    {
        var records = new List<ImageRecord>();
        foreach (var batch in PathVariants(paths).Chunk(200))
        {
            using var command = PageCommand("SELECT * FROM images", sourceId, batch);
            using var reader = command.ExecuteReader();
            while (reader.Read()) records.Add(ReadRecord(reader));
        }
        return records.DistinctBy(record => record.Id).ToArray();
    }

    public IReadOnlyList<VideoRecord> GetScanVideosByPaths(long sourceId, IEnumerable<string> paths)
    {
        var records = new List<VideoRecord>();
        foreach (var batch in PathVariants(paths).Chunk(200))
        {
            using var command = PageCommand("SELECT id,source_id,path,file_size,modified_utc,duration_seconds,width,height,taken_utc,gps,status,person_analysis_status,person_analyzed_utc,sha256 FROM videos", sourceId, batch);
            using var reader = command.ExecuteReader();
            while (reader.Read()) records.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3),
                Parse(reader.GetString(4)), reader.IsDBNull(5) ? null : reader.GetDouble(5), reader.GetInt32(6), reader.GetInt32(7),
                reader.IsDBNull(8) ? null : Parse(reader.GetString(8)), reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetString(10))
            {
                PersonAnalysisStatus = reader.IsDBNull(11) ? null : reader.GetString(11),
                PersonAnalyzedUtc = reader.IsDBNull(12) ? null : reader.GetString(12), Sha256 = reader.IsDBNull(13) ? null : reader.GetString(13)
            });
        }
        return records.DistinctBy(record => record.Id).ToArray();
    }

    private SqliteCommand PageCommand(string select, long sourceId, string[] paths)
    {
        var command = Command(select + " WHERE source_id=$source AND path COLLATE NOCASE IN (" + string.Join(',', paths.Select((_, index) => "$p" + index)) + ")");
        command.Parameters.AddWithValue("$source", sourceId);
        for (var index = 0; index < paths.Length; index++) command.Parameters.AddWithValue("$p" + index, paths[index]);
        return command;
    }

    private static IEnumerable<string> PathVariants(IEnumerable<string> paths) => paths.SelectMany(path =>
        new[] { path, DirectoryMediaImport.Normalize(path), path.Replace('/', '\\'), path.Replace('\\', '/') }).Distinct(StringComparer.OrdinalIgnoreCase);
}
