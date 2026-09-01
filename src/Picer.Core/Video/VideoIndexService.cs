using Picer.Core.Data;
using Picer.Core.Vision;

namespace Picer.Core.Video;

public sealed record VideoRecord(long Id, long SourceId, string Path, long FileSize, DateTime ModifiedUtc,
    double? DurationSeconds, int Width, int Height, DateTime? TakenUtc, string? Gps, string Status);

public static class VideoFormats
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".wmv", ".mts", ".m2ts", ".3gp", ".webm" };
    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path));
}

public sealed class VideoRepository(PicerDatabase database)
{
    public VideoRecord Upsert(long sourceId, string path)
    {
        var info = new FileInfo(path);
        var fullPath = Path.GetFullPath(path);
        using (var current = database.Command("SELECT id,file_size,modified_utc,status FROM videos WHERE path=$path"))
        {
            current.Parameters.AddWithValue("$path", fullPath);
            using var reader = current.ExecuteReader();
            if (reader.Read() && reader.GetInt64(1) == info.Length &&
                DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind) == info.LastWriteTimeUtc &&
                reader.GetString(3) == "active")
                return new VideoRecord(reader.GetInt64(0), sourceId, fullPath, info.Length, info.LastWriteTimeUtc,
                    null, 0, 0, null, null, "active");
        }
        using var command = database.Command("""
            INSERT INTO videos(source_id,path,file_size,modified_utc,status,indexed_utc)
            VALUES($source,$path,$size,$modified,'active',$now)
            ON CONFLICT(path) DO UPDATE SET source_id=excluded.source_id,file_size=excluded.file_size,
              modified_utc=excluded.modified_utc,status='active',indexed_utc=excluded.indexed_utc
            RETURNING id
            """);
        command.Parameters.AddWithValue("$source", sourceId); command.Parameters.AddWithValue("$path", fullPath);
        command.Parameters.AddWithValue("$size", info.Length); command.Parameters.AddWithValue("$modified", info.LastWriteTimeUtc.ToString("o"));
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        var id = (long)command.ExecuteScalar()!;
        return new VideoRecord(id, sourceId, fullPath, info.Length, info.LastWriteTimeUtc, null, 0, 0, null, null, "active");
    }

    public IReadOnlyList<VideoRecord> GetAll()
    {
        using var command = database.Command("SELECT id,source_id,path,file_size,modified_utc,duration_seconds,width,height,taken_utc,gps,status FROM videos ORDER BY path");
        using var reader = command.ExecuteReader(); var result = new List<VideoRecord>();
        while (reader.Read()) result.Add(new VideoRecord(reader.GetInt64(0),reader.GetInt64(1),reader.GetString(2),reader.GetInt64(3),
            DateTime.Parse(reader.GetString(4),null,System.Globalization.DateTimeStyles.RoundtripKind),reader.IsDBNull(5)?null:reader.GetDouble(5),
            reader.GetInt32(6),reader.GetInt32(7),reader.IsDBNull(8)?null:DateTime.Parse(reader.GetString(8)),reader.IsDBNull(9)?null:reader.GetString(9),reader.GetString(10)));
        return result;
    }

    public IReadOnlyList<VideoRecord> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return GetAll();
        using var command=database.Command("""
            SELECT v.id,v.source_id,v.path,v.file_size,v.modified_utc,v.duration_seconds,v.width,v.height,v.taken_utc,v.gps,v.status
            FROM videos v LEFT JOIN video_transcripts tr ON tr.video_id=v.id
            LEFT JOIN video_summaries vs ON vs.video_id=v.id
            WHERE v.status='active' AND (v.path LIKE $query OR tr.transcript_json LIKE $query OR vs.result_json LIKE $query)
            ORDER BY v.path
            """);
        command.Parameters.AddWithValue("$query","%"+query.Trim()+"%"); using var reader=command.ExecuteReader(); var result=new List<VideoRecord>();
        while(reader.Read()) result.Add(new VideoRecord(reader.GetInt64(0),reader.GetInt64(1),reader.GetString(2),reader.GetInt64(3),DateTime.Parse(reader.GetString(4),null,System.Globalization.DateTimeStyles.RoundtripKind),reader.IsDBNull(5)?null:reader.GetDouble(5),reader.GetInt32(6),reader.GetInt32(7),reader.IsDBNull(8)?null:DateTime.Parse(reader.GetString(8)),reader.IsDBNull(9)?null:reader.GetString(9),reader.GetString(10)));
        return result;
    }

    public void SaveProbe(long videoId, VideoProbeResult probe)
    {
        using var command = database.Command("UPDATE videos SET duration_seconds=$duration,width=$width,height=$height WHERE id=$id");
        command.Parameters.AddWithValue("$duration", probe.DurationSeconds); command.Parameters.AddWithValue("$width", probe.Width);
        command.Parameters.AddWithValue("$height", probe.Height); command.Parameters.AddWithValue("$id", videoId); command.ExecuteNonQuery();
    }

    public void SaveKeyframes(long videoId, IReadOnlyList<VideoKeyframe> frames)
    {
        using var transaction = database.BeginTransaction();
        using (var clear = database.Command("DELETE FROM video_keyframes WHERE video_id=$video"))
        { clear.Transaction = transaction; clear.Parameters.AddWithValue("$video", videoId); clear.ExecuteNonQuery(); }
        foreach (var frame in frames)
        {
            using var insert = database.Command("INSERT INTO video_keyframes(video_id,timestamp_seconds,path,dhash,created_utc) VALUES($video,$time,$path,$hash,$now)");
            insert.Transaction = transaction; insert.Parameters.AddWithValue("$video", videoId);
            insert.Parameters.AddWithValue("$time", frame.TimestampSeconds); insert.Parameters.AddWithValue("$path", frame.Path);
            insert.Parameters.AddWithValue("$hash", frame.DifferenceHash);
            insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o")); insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<VideoKeyframe> GetKeyframes(long videoId, bool existingOnly = true)
    {
        using var command = database.Command("SELECT timestamp_seconds,path,dhash FROM video_keyframes WHERE video_id=$video ORDER BY timestamp_seconds");
        command.Parameters.AddWithValue("$video", videoId); using var reader = command.ExecuteReader();
        var result = new List<VideoKeyframe>();
        while (reader.Read())
        {
            var frame = new VideoKeyframe(reader.GetDouble(0), reader.GetString(1), reader.GetInt64(2));
            if (!existingOnly || File.Exists(frame.Path)) result.Add(frame);
        }
        return result;
    }

    public void SaveTranscript(long videoId, string modelVersion, string transcriptJson)
    {
        using var command=database.Command("""
            INSERT INTO video_transcripts(video_id,model_version,transcript_json,created_utc) VALUES($video,$model,$json,$now)
            ON CONFLICT(video_id) DO UPDATE SET model_version=excluded.model_version,transcript_json=excluded.transcript_json,created_utc=excluded.created_utc
            """);
        command.Parameters.AddWithValue("$video",videoId); command.Parameters.AddWithValue("$model",modelVersion);
        command.Parameters.AddWithValue("$json",transcriptJson); command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("o")); command.ExecuteNonQuery();
    }

    public string? GetTranscript(long videoId)
    {
        using var command=database.Command("SELECT transcript_json FROM video_transcripts WHERE video_id=$video");
        command.Parameters.AddWithValue("$video",videoId); return command.ExecuteScalar() as string;
    }

    public void SaveSummary(long videoId,ICloudVisionAnalyzer analyzer,VideoRecord video,CloudVisionResult result)
    {
        using var command=database.Command("""
            INSERT INTO video_summaries(video_id,provider,model_version,prompt_version,source_file_size,source_modified_utc,summary,result_json,input_tokens,output_tokens,estimated_cost_yuan,created_utc)
            VALUES($video,$provider,$model,$prompt,$size,$modified,$summary,$json,$input,$output,$cost,$now)
            ON CONFLICT(video_id) DO UPDATE SET provider=excluded.provider,model_version=excluded.model_version,prompt_version=excluded.prompt_version,source_file_size=excluded.source_file_size,source_modified_utc=excluded.source_modified_utc,summary=excluded.summary,result_json=excluded.result_json,input_tokens=excluded.input_tokens,output_tokens=excluded.output_tokens,estimated_cost_yuan=excluded.estimated_cost_yuan,created_utc=excluded.created_utc
            """);command.Parameters.AddWithValue("$video",videoId);command.Parameters.AddWithValue("$provider",analyzer.Provider);command.Parameters.AddWithValue("$model",analyzer.ModelVersion);command.Parameters.AddWithValue("$prompt",analyzer.PromptVersion);command.Parameters.AddWithValue("$size",video.FileSize);command.Parameters.AddWithValue("$modified",video.ModifiedUtc.ToString("o"));command.Parameters.AddWithValue("$summary",result.Summary);command.Parameters.AddWithValue("$json",System.Text.Json.JsonSerializer.Serialize(result));command.Parameters.AddWithValue("$input",result.Usage?.InputTokens??0);command.Parameters.AddWithValue("$output",result.Usage?.OutputTokens??0);command.Parameters.AddWithValue("$cost",result.Usage?.EstimatedCostYuan??0);command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("o"));command.ExecuteNonQuery();
    }
    public string? GetSummary(long videoId){using var command=database.Command("SELECT summary FROM video_summaries WHERE video_id=$video");command.Parameters.AddWithValue("$video",videoId);return command.ExecuteScalar() as string;}
}

public sealed class VideoIndexService(PicerDatabase database, VideoRepository repository)
{
    public int ScanLocalSources(CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var source in database.GetSources())
        {
            if (!Directory.Exists(source.Path)) continue;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
            };
            foreach (var path in Directory.EnumerateFiles(source.Path, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!VideoFormats.IsSupported(path)) continue;
                try { repository.Upsert(source.Id, path); count++; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return count;
    }

    public Task<int> ScanLocalSourcesAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => ScanLocalSources(cancellationToken), cancellationToken);
}

public sealed class FfmpegLocator(string? applicationBaseDirectory = null)
{
    public string? Find()
    {
        var bundled = Path.Combine(applicationBaseDirectory ?? AppContext.BaseDirectory, "runtime", "ffmpeg", "ffmpeg.exe");
        if (File.Exists(bundled)) return bundled;
        var configured = Environment.GetEnvironmentVariable("PICER_FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        return null;
    }
}
