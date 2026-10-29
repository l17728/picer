using System.Globalization;
using Picer.Core.Data;
using Picer.Core.Video;

namespace Picer.Core.Gateway;

public sealed record GatewayVideoQuery(long? SourceId = null, string? DirectoryPath = null, string? Query = null, bool IncludeMissing = true);
public sealed record GatewayDirectorySummary(long SourceId, string Path, int Images, int Videos);

public static class GatewayBrowseQueries
{
    public static GatewayVideoPage Videos(PicerDatabase database, GatewayVideoQuery query, int pageSize,
        string? cursor, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var offset = int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        var size = Math.Clamp(pageSize, 1, 500);
        var prefix = string.IsNullOrWhiteSpace(query.DirectoryPath) ? null : query.DirectoryPath.Replace('\\', '/').TrimEnd('/') + "/";
        const string where = """
            WHERE ($source IS NULL OR source_id=$source) AND ($missing=1 OR status='active')
            AND ($prefix IS NULL OR substr(replace(path,char(92),'/'),1,length($prefix))=$prefix COLLATE NOCASE)
            AND ($query='' OR instr(lower(path),lower($query))>0)
            """;
        using var command = database.Command("SELECT COUNT(*) FROM videos " + where);
        command.Parameters.AddWithValue("$source", (object?)query.SourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$prefix", (object?)prefix ?? DBNull.Value);
        command.Parameters.AddWithValue("$query", query.Query?.Trim() ?? "");
        command.Parameters.AddWithValue("$missing", query.IncludeMissing ? 1 : 0);
        using var registration = token.Register(command.Cancel);
        var total = Convert.ToInt32(command.ExecuteScalar());
        command.CommandText = "SELECT id,source_id,path,file_size,modified_utc,duration_seconds,width,height,taken_utc,gps,status,person_analysis_status,person_analyzed_utc,sha256 FROM videos " + where + " ORDER BY path,id LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", size);
        command.Parameters.AddWithValue("$offset", offset);
        var result = new List<VideoRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            result.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3),
                DateTime.Parse(reader.GetString(4), null, DateTimeStyles.RoundtripKind), reader.IsDBNull(5) ? null : reader.GetDouble(5),
                reader.GetInt32(6), reader.GetInt32(7), reader.IsDBNull(8) ? null : DateTime.Parse(reader.GetString(8), null, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetString(10))
            {
                PersonAnalysisStatus = reader.IsDBNull(11) ? null : reader.GetString(11),
                PersonAnalyzedUtc = reader.IsDBNull(12) ? null : reader.GetString(12), Sha256 = reader.IsDBNull(13) ? null : reader.GetString(13)
            });
        }
        return new(result, offset + result.Count < total ? (offset + result.Count).ToString(CultureInfo.InvariantCulture) : null, total);
    }

    public static IReadOnlyList<GatewayDirectorySummary> Directories(PicerDatabase database, CancellationToken token = default)
    {
        using var command = database.Command("SELECT source_id,path,0 FROM images WHERE status='active' UNION ALL SELECT source_id,path,1 FROM videos WHERE status='active'");
        using var registration = token.Register(command.Cancel);
        using var reader = command.ExecuteReader();
        var counts = new Dictionary<(long Source, string Path), (int Images, int Videos)>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            var path = reader.GetString(1).Replace('\\', '/');
            var separator = path.LastIndexOf('/');
            if (separator < 0) continue;
            var key = (reader.GetInt64(0), path[..separator]);
            var count = counts.GetValueOrDefault(key);
            counts[key] = reader.GetInt32(2) == 0 ? (count.Images + 1, count.Videos) : (count.Images, count.Videos + 1);
        }
        return counts.Select(item => new GatewayDirectorySummary(item.Key.Source, item.Key.Path, item.Value.Images, item.Value.Videos))
            .OrderBy(item => item.SourceId).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
