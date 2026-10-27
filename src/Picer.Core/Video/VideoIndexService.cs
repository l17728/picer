using Picer.Core.Data;
using Picer.Core.Vision;
using Picer.Core.Search;

namespace Picer.Core.Video;

public sealed record VideoRecord(long Id, long SourceId, string Path, long FileSize, DateTime ModifiedUtc,
    double? DurationSeconds, int Width, int Height, DateTime? TakenUtc, string? Gps, string Status)
{
    public string? Sha256 { get; init; }
    public string? PersonAnalysisStatus { get; init; }
    public string? PersonAnalyzedUtc { get; init; }
}
/// <summary>视频人物、对话和摘要分析状态的独立汇总。</summary>
public sealed record VideoFaceReviewSummary(
    int Total,
    int Pending,
    int Confirmed,
    int Ignored,
    string? AnalysisStatus = null,
    bool HasTranscript = false,
    bool HasSummary = false);

public static class VideoFormats
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".wmv", ".mts", ".m2ts", ".3gp", ".webm" };
    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path));
}

public sealed class VideoRepository(PicerDatabase database)
{
    internal string DatabaseIdentity => database.DatabasePath == ":memory:"
        ? "memory:" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(database)
        : Path.GetFullPath(database.DatabasePath);

    internal CloudVisionResult? GetCurrentSummaryResult(VideoRecord video, ICloudVisionAnalyzer analyzer)
    {
        if (!HasCurrentSummary(video)) return null;
        using var command = database.Command("SELECT result_json FROM video_summaries WHERE video_id=$id AND provider=$provider AND model_version=$model AND prompt_version=$prompt");
        command.Parameters.AddWithValue("$id", GetContentId(video.Id));
        command.Parameters.AddWithValue("$provider", analyzer.Provider);
        command.Parameters.AddWithValue("$model", analyzer.ModelVersion);
        command.Parameters.AddWithValue("$prompt", analyzer.PromptVersion);
        if (command.ExecuteScalar() is not string json) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<CloudVisionResult>(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }
    public VideoContentRepository Contents { get; } = new(database);
    public long GetContentId(long videoId) => Contents.GetContentId(videoId);

    public IReadOnlySet<long> GetCurrentSummaryIds(IReadOnlyList<VideoRecord> videos, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (videos.Count == 0) return new HashSet<long>();
        using var command = database.Command("""
            SELECT v.id FROM videos v JOIN json_each($ids) requested ON requested.value=v.id
            WHERE EXISTS(SELECT 1 FROM video_summaries s
                WHERE s.video_id=v.content_id AND length(trim(s.summary))>0
                  AND s.source_file_size=v.file_size
                  AND (s.source_modified_utc=v.modified_utc OR EXISTS(
                      SELECT 1 FROM video_contents c WHERE c.id=v.content_id AND length(c.sha256)=64)))
            """);
        command.Parameters.AddWithValue("$ids", System.Text.Json.JsonSerializer.Serialize(videos.Select(video => video.Id)));
        var result = new HashSet<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) { token.ThrowIfCancellationRequested(); result.Add(reader.GetInt64(0)); }
        return result;
    }
    private const string MirroredVideoUpsertSql = """
        INSERT INTO videos(id,source_id,path,file_size,modified_utc,duration_seconds,width,height,taken_utc,gps,status,indexed_utc,person_analysis_status,person_analyzed_utc,sha256)
        VALUES($id,$source,$path,$size,$modified,$duration,$width,$height,$taken,$gps,$status,$now,$analysis,$analyzed,$sha)
        ON CONFLICT(id) DO UPDATE SET source_id=excluded.source_id,path=excluded.path,file_size=excluded.file_size,
          modified_utc=excluded.modified_utc,duration_seconds=excluded.duration_seconds,width=excluded.width,
          height=excluded.height,taken_utc=excluded.taken_utc,gps=excluded.gps,status=excluded.status,indexed_utc=excluded.indexed_utc,
          person_analysis_status=COALESCE(excluded.person_analysis_status,videos.person_analysis_status),
          person_analyzed_utc=COALESCE(excluded.person_analyzed_utc,videos.person_analyzed_utc),
          sha256=COALESCE(excluded.sha256,videos.sha256)
        """;

    public void UpsertMirrored(VideoRecord video, long sourceId)
        => UpsertMirroredBatch([video], sourceId);

    /// <summary>Upserts a remote video page in one short SQLite transaction.
    /// The former per-row autocommit path caused hundreds of fsyncs per page
    /// and monopolized the shared UI database gate for minutes.</summary>
    public void UpsertMirroredBatch(IReadOnlyList<VideoRecord> videos, long sourceId)
    {
        if (videos.Count == 0) return;
        using var transaction = database.BeginTransaction();
        using var command = database.Command(MirroredVideoUpsertSql);
        command.Transaction = transaction;
        command.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
        command.Parameters.Add("$source", Microsoft.Data.Sqlite.SqliteType.Integer);
        command.Parameters.Add("$path", Microsoft.Data.Sqlite.SqliteType.Text);
        command.Parameters.Add("$size", Microsoft.Data.Sqlite.SqliteType.Integer);
        command.Parameters.Add("$modified", Microsoft.Data.Sqlite.SqliteType.Text);
        command.Parameters.Add("$duration", Microsoft.Data.Sqlite.SqliteType.Real);
        command.Parameters.Add("$width", Microsoft.Data.Sqlite.SqliteType.Integer);
        command.Parameters.Add("$height", Microsoft.Data.Sqlite.SqliteType.Integer);
        command.Parameters.Add("$taken", Microsoft.Data.Sqlite.SqliteType.Text);
        command.Parameters.Add("$gps", Microsoft.Data.Sqlite.SqliteType.Text);
        command.Parameters.Add("$status", Microsoft.Data.Sqlite.SqliteType.Text);
        command.Parameters.Add("$now", Microsoft.Data.Sqlite.SqliteType.Text);
        command.Parameters.Add("$analysis", Microsoft.Data.Sqlite.SqliteType.Text);
        command.Parameters.Add("$analyzed", Microsoft.Data.Sqlite.SqliteType.Text);
        command.Parameters.Add("$sha", Microsoft.Data.Sqlite.SqliteType.Text);
        command.Prepare();
        foreach (var video in videos)
        {
            InvalidateChangedContent(video.Path, video.Id, video.FileSize, video.ModifiedUtc, video.Sha256);
            SetValue(command, "$id", video.Id);
            SetValue(command, "$source", sourceId);
            SetValue(command, "$path", video.Path);
            SetValue(command, "$size", video.FileSize);
            SetValue(command, "$modified", video.ModifiedUtc.ToUniversalTime().ToString("o"));
            SetValue(command, "$duration", (object?)video.DurationSeconds ?? DBNull.Value);
            SetValue(command, "$width", video.Width);
            SetValue(command, "$height", video.Height);
            SetValue(command, "$taken", (object?)video.TakenUtc?.ToUniversalTime().ToString("o") ?? DBNull.Value);
            SetValue(command, "$gps", (object?)video.Gps ?? DBNull.Value);
            SetValue(command, "$status", video.Status);
            SetValue(command, "$now", DateTime.UtcNow.ToString("o"));
            SetValue(command, "$analysis", (object?)video.PersonAnalysisStatus ?? DBNull.Value);
            SetValue(command, "$analyzed", (object?)video.PersonAnalyzedUtc ?? DBNull.Value);
            SetValue(command, "$sha", (object?)video.Sha256?.ToLowerInvariant() ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void BindMirroredVideo(Microsoft.Data.Sqlite.SqliteCommand command, VideoRecord video, long sourceId)
    {
        command.Parameters.AddWithValue("$id", video.Id); command.Parameters.AddWithValue("$source", sourceId);
        command.Parameters.AddWithValue("$path", video.Path); command.Parameters.AddWithValue("$size", video.FileSize);
        command.Parameters.AddWithValue("$modified", video.ModifiedUtc.ToUniversalTime().ToString("o"));
        command.Parameters.AddWithValue("$duration", (object?)video.DurationSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$width", video.Width); command.Parameters.AddWithValue("$height", video.Height);
        command.Parameters.AddWithValue("$taken", (object?)video.TakenUtc?.ToUniversalTime().ToString("o") ?? DBNull.Value);
        command.Parameters.AddWithValue("$gps", (object?)video.Gps ?? DBNull.Value); command.Parameters.AddWithValue("$status", video.Status);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        command.Parameters.AddWithValue("$analysis", (object?)video.PersonAnalysisStatus ?? DBNull.Value);
        command.Parameters.AddWithValue("$analyzed", (object?)video.PersonAnalyzedUtc ?? DBNull.Value);
    }

    private static void SetValue(Microsoft.Data.Sqlite.SqliteCommand command, string name, object value)
    {
        command.Parameters[name].Value = value;
    }

    private void InvalidateChangedContent(string path, long id, long size, DateTime modified, string? sha256 = null)
    {
        if (sha256 is { Length: 64 } && sha256.All(Uri.IsHexDigit))
        {
            using var signature = database.Command("""
                UPDATE video_summaries SET source_file_size=$size,source_modified_utc=$modified
                WHERE video_id IN (SELECT v.content_id FROM videos v WHERE (v.id=$id OR v.path=$path) AND v.sha256=$sha COLLATE NOCASE
                    AND video_summaries.source_file_size=v.file_size AND video_summaries.source_modified_utc=v.modified_utc)
                """);
            signature.Parameters.AddWithValue("$id", id); signature.Parameters.AddWithValue("$path", path);
            signature.Parameters.AddWithValue("$size", size); signature.Parameters.AddWithValue("$modified", modified.ToUniversalTime().ToString("o"));
            signature.Parameters.AddWithValue("$sha", sha256); signature.ExecuteNonQuery();
        }
        using var find = database.Command("SELECT id FROM videos WHERE (id=$id OR path=$path) AND (file_size<>$size OR modified_utc<>$modified OR ($sha IS NOT NULL AND length(sha256)=64 AND sha256<>$sha COLLATE NOCASE)) AND ($sha IS NULL OR sha256 IS NULL OR sha256<>$sha COLLATE NOCASE) LIMIT 1");
        find.Parameters.AddWithValue("$id", id); find.Parameters.AddWithValue("$path", path);
        find.Parameters.AddWithValue("$size", size); find.Parameters.AddWithValue("$modified", modified.ToUniversalTime().ToString("o"));
        find.Parameters.AddWithValue("$sha", sha256 is { Length: 64 } && sha256.All(Uri.IsHexDigit) ? sha256 : DBNull.Value);
        if (find.ExecuteScalar() is not long changed) return;
        Contents.DetachChangedReference(changed);
    }
    public void UpsertRemote(VideoRecord video, long sourceId, bool preserveIdentity = false)
    {
        using var savepoint = database.Command("SAVEPOINT video_remote_upsert"); savepoint.ExecuteNonQuery();
        try
        {
        InvalidateChangedContent(video.Path, 0, video.FileSize, video.ModifiedUtc, video.Sha256);
        using var command = database.Command($"""
            INSERT INTO videos({(preserveIdentity ? "id," : "")}source_id,path,file_size,modified_utc,duration_seconds,width,height,taken_utc,gps,status,indexed_utc,sha256)
            VALUES({(preserveIdentity ? "$id," : "")}$source,$path,$size,$modified,$duration,$width,$height,$taken,$gps,$status,$now,$sha)
            ON CONFLICT({(preserveIdentity ? "id" : "path")}) DO UPDATE SET source_id=excluded.source_id,path=excluded.path,file_size=excluded.file_size,
              modified_utc=excluded.modified_utc,duration_seconds=excluded.duration_seconds,width=excluded.width,
              height=excluded.height,taken_utc=excluded.taken_utc,gps=excluded.gps,status=excluded.status,indexed_utc=excluded.indexed_utc,sha256=COALESCE(excluded.sha256,videos.sha256)
            """);
        if (preserveIdentity) command.Parameters.AddWithValue("$id", video.Id);
        command.Parameters.AddWithValue("$source", sourceId); command.Parameters.AddWithValue("$path", video.Path);
        command.Parameters.AddWithValue("$size", video.FileSize); command.Parameters.AddWithValue("$modified", video.ModifiedUtc.ToUniversalTime().ToString("o"));
        command.Parameters.AddWithValue("$duration", (object?)video.DurationSeconds ?? DBNull.Value); command.Parameters.AddWithValue("$width", video.Width);
        command.Parameters.AddWithValue("$height", video.Height); command.Parameters.AddWithValue("$taken", (object?)video.TakenUtc?.ToUniversalTime().ToString("o") ?? DBNull.Value);
        command.Parameters.AddWithValue("$gps", (object?)video.Gps ?? DBNull.Value); command.Parameters.AddWithValue("$status", video.Status);
        command.Parameters.AddWithValue("$sha", (object?)video.Sha256?.ToLowerInvariant() ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o")); command.ExecuteNonQuery();
        using var release = database.Command("RELEASE video_remote_upsert"); release.ExecuteNonQuery();
        }
        catch
        {
            using var rollback = database.Command("ROLLBACK TO video_remote_upsert; RELEASE video_remote_upsert"); rollback.ExecuteNonQuery();
            throw;
        }
    }
    public VideoRecord Upsert(long sourceId, string path, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        var fullPath = Path.GetFullPath(path);
        using (var current = database.Command("SELECT id,file_size,modified_utc,status,sha256 FROM videos WHERE path=$path"))
        {
            current.Parameters.AddWithValue("$path", fullPath);
            using var reader = current.ExecuteReader();
            if (reader.Read() && reader.GetInt64(1) == info.Length &&
                DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind) == info.LastWriteTimeUtc &&
                reader.GetString(3) == "active" && !reader.IsDBNull(4))
                return new VideoRecord(reader.GetInt64(0), sourceId, fullPath, info.Length, info.LastWriteTimeUtc,
                    null, 0, 0, null, null, "active") { Sha256 = reader.GetString(4) };
        }
        var size = info.Length; var modified = info.LastWriteTimeUtc;
        string hash;
        using (var stream = File.OpenRead(fullPath))
        using (var digest = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256))
        {
            var buffer = new byte[81920];
            int count;
            while ((count = stream.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                digest.AppendData(buffer, 0, count);
            }
            cancellationToken.ThrowIfCancellationRequested();
            hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
        }
        info.Refresh();
        if (info.Length != size || info.LastWriteTimeUtc != modified) throw new IOException("video_changed_during_hash");
        using var transaction = database.BeginTransaction();
        InvalidateChangedContent(fullPath, 0, info.Length, info.LastWriteTimeUtc, hash);
        using var command = database.Command("""
            INSERT INTO videos(source_id,path,file_size,modified_utc,status,indexed_utc,sha256)
            VALUES($source,$path,$size,$modified,'active',$now,$sha)
            ON CONFLICT(path) DO UPDATE SET source_id=excluded.source_id,file_size=excluded.file_size,
              modified_utc=excluded.modified_utc,status='active',indexed_utc=excluded.indexed_utc,sha256=excluded.sha256
            RETURNING id
            """);
        command.Parameters.AddWithValue("$source", sourceId); command.Parameters.AddWithValue("$path", fullPath);
        command.Parameters.AddWithValue("$size", info.Length); command.Parameters.AddWithValue("$modified", info.LastWriteTimeUtc.ToString("o"));
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        command.Parameters.AddWithValue("$sha", hash);
        var id = (long)command.ExecuteScalar()!;
        transaction.Commit();
        return new VideoRecord(id, sourceId, fullPath, info.Length, info.LastWriteTimeUtc, null, 0, 0, null, null, "active") { Sha256 = hash };
    }

    public IReadOnlyList<VideoRecord> GetAll()
    {
        using var command = database.Command("SELECT id,source_id,path,file_size,modified_utc,duration_seconds,width,height,taken_utc,gps,status,person_analysis_status,person_analyzed_utc,sha256 FROM videos ORDER BY path");
        using var reader = command.ExecuteReader(); var result = new List<VideoRecord>();
        while (reader.Read()) result.Add(new VideoRecord(reader.GetInt64(0),reader.GetInt64(1),reader.GetString(2),reader.GetInt64(3),
            DateTime.Parse(reader.GetString(4),null,System.Globalization.DateTimeStyles.RoundtripKind),reader.IsDBNull(5)?null:reader.GetDouble(5),
            reader.GetInt32(6),reader.GetInt32(7),reader.IsDBNull(8)?null:DateTime.Parse(reader.GetString(8)),reader.IsDBNull(9)?null:reader.GetString(9),reader.GetString(10))
            { Sha256 = reader.IsDBNull(13) ? null : reader.GetString(13), PersonAnalysisStatus = reader.IsDBNull(11) ? null : reader.GetString(11), PersonAnalyzedUtc = reader.IsDBNull(12) ? null : reader.GetString(12) });
        return result;
    }

    public VideoRecord? Get(long id)
    {
        using var command = database.Command("SELECT id,source_id,path,file_size,modified_utc,duration_seconds,width,height,taken_utc,gps,status,person_analysis_status,person_analyzed_utc,sha256 FROM videos WHERE id=$id");
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3),
            DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind), reader.IsDBNull(5) ? null : reader.GetDouble(5),
            reader.GetInt32(6), reader.GetInt32(7), reader.IsDBNull(8) ? null : DateTime.Parse(reader.GetString(8)), reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetString(10))
        { PersonAnalysisStatus = reader.IsDBNull(11) ? null : reader.GetString(11), PersonAnalyzedUtc = reader.IsDBNull(12) ? null : reader.GetString(12), Sha256 = reader.IsDBNull(13) ? null : reader.GetString(13) };
    }

    public IReadOnlyDictionary<long, int> GetKeyframeCounts()
    {
        using var command = database.Command("SELECT v.id,COUNT(*) FROM videos v JOIN video_keyframes k ON k.video_id=v.content_id GROUP BY v.id");
        using var reader = command.ExecuteReader();
        var result = new Dictionary<long, int>();
        while (reader.Read()) result[reader.GetInt64(0)] = reader.GetInt32(1);
        return result;
    }

    public IReadOnlyList<VideoRecord> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return GetAll();
        using var command=database.Command("""
            SELECT v.id,v.source_id,v.path,v.file_size,v.modified_utc,v.duration_seconds,v.width,v.height,v.taken_utc,v.gps,v.status
            FROM videos v LEFT JOIN video_transcripts tr ON tr.video_id=v.content_id
            LEFT JOIN video_summaries vs ON vs.video_id=v.content_id
            WHERE v.status='active' AND (v.path LIKE $query OR tr.transcript_json LIKE $query OR vs.result_json LIKE $query)
            ORDER BY v.path
            """);
        command.Parameters.AddWithValue("$query","%"+query.Trim()+"%"); using var reader=command.ExecuteReader(); var result=new List<VideoRecord>();
        while(reader.Read()) result.Add(new VideoRecord(reader.GetInt64(0),reader.GetInt64(1),reader.GetString(2),reader.GetInt64(3),DateTime.Parse(reader.GetString(4),null,System.Globalization.DateTimeStyles.RoundtripKind),reader.IsDBNull(5)?null:reader.GetDouble(5),reader.GetInt32(6),reader.GetInt32(7),reader.IsDBNull(8)?null:DateTime.Parse(reader.GetString(8)),reader.IsDBNull(9)?null:reader.GetString(9),reader.GetString(10)));
        return result;
    }

    public void SaveProbe(long videoId, VideoProbeResult probe, VideoRecord? expected = null)
    {
        using var transaction = database.BeginTransaction();
        if (expected is not null && !IsCurrentContent(videoId, expected)) throw new InvalidOperationException("video_content_changed");
        using var command = database.Command("UPDATE videos SET duration_seconds=$duration,width=$width,height=$height WHERE content_id=(SELECT content_id FROM videos WHERE id=$id)");
        command.Parameters.AddWithValue("$duration", probe.DurationSeconds); command.Parameters.AddWithValue("$width", probe.Width);
        command.Parameters.AddWithValue("$height", probe.Height); command.Parameters.AddWithValue("$id", videoId); command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void SaveKeyframes(long videoId, IReadOnlyList<VideoKeyframe> frames, VideoRecord? expected = null)
    {
        using var transaction = database.BeginTransaction();
        if (expected is not null && !IsCurrentContent(videoId, expected)) throw new InvalidOperationException("video_content_changed");
        using (var clear = database.Command("DELETE FROM video_keyframes WHERE video_id=$video"))
        { clear.Transaction = transaction; clear.Parameters.AddWithValue("$video", GetContentId(videoId)); clear.ExecuteNonQuery(); }
        foreach (var frame in frames)
        {
            using var insert = frame.Id > 0
                ? database.Command("INSERT INTO video_keyframes(id,video_id,timestamp_seconds,path,dhash,created_utc) VALUES($id,$video,$time,$path,$hash,$now)")
                : database.Command("INSERT INTO video_keyframes(video_id,timestamp_seconds,path,dhash,created_utc) VALUES($video,$time,$path,$hash,$now)");
            insert.Transaction = transaction; insert.Parameters.AddWithValue("$video", GetContentId(videoId));
            if (frame.Id > 0) insert.Parameters.AddWithValue("$id", frame.Id);
            insert.Parameters.AddWithValue("$time", frame.TimestampSeconds); insert.Parameters.AddWithValue("$path", frame.Path);
            insert.Parameters.AddWithValue("$hash", frame.DifferenceHash);
            insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o")); insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyList<VideoKeyframe> GetKeyframes(long videoId, bool existingOnly = true)
    {
        using var command = database.Command("SELECT timestamp_seconds,path,dhash,id FROM video_keyframes WHERE video_id=$video ORDER BY timestamp_seconds");
        command.Parameters.AddWithValue("$video", GetContentId(videoId)); using var reader = command.ExecuteReader();
        var result = new List<VideoKeyframe>();
        while (reader.Read())
        {
            var frame = new VideoKeyframe(reader.GetDouble(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3));
            if (!existingOnly || File.Exists(frame.Path)) result.Add(frame);
        }
        return result;
    }

    public IReadOnlyDictionary<long, string> GetFirstKeyframePaths()
    {
        using var command = database.Command("""
            SELECT v.id,k.path FROM video_keyframes k JOIN videos v ON v.content_id=k.video_id
            WHERE k.id=(SELECT first.id FROM video_keyframes first
                        WHERE first.video_id=k.video_id ORDER BY first.timestamp_seconds,first.id LIMIT 1)
            """);
        var result = new Dictionary<long, string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var path = reader.GetString(1);
            if (File.Exists(path)) result[reader.GetInt64(0)] = path;
        }
        return result;
    }

    public IReadOnlyDictionary<long, VideoFaceReviewSummary> GetFaceReviewSummaries()
    {
        using var command = database.Command("""
            SELECT v.id,COUNT(vf.id),
                   SUM(CASE WHEN vf.id IS NOT NULL AND vf.assignment_state IS NULL THEN 1 ELSE 0 END),
                   SUM(CASE WHEN vf.assignment_state='confirmed' THEN 1 ELSE 0 END),
                   SUM(CASE WHEN vf.assignment_state='ignored' THEN 1 ELSE 0 END),v.person_analysis_status,
                   EXISTS(SELECT 1 FROM video_transcripts tr WHERE tr.video_id=v.content_id),
                   EXISTS(SELECT 1 FROM video_summaries vs WHERE vs.video_id=v.content_id)
            FROM videos v LEFT JOIN video_effective_faces vf ON vf.reference_id=v.id
            WHERE v.status='active' GROUP BY v.id,v.person_analysis_status
            """);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<long, VideoFaceReviewSummary>();
        while (reader.Read()) result[reader.GetInt64(0)] = new(
            reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            Convert.ToInt64(reader.GetValue(6)) != 0, Convert.ToInt64(reader.GetValue(7)) != 0);
        return result;
    }

    public void SavePersonAnalysisStatus(long videoId, string status, VideoRecord? expected = null)
    {
        using var transaction = database.BeginTransaction();
        if (expected is not null && !IsCurrentContent(videoId, expected)) return;
        if (status is not ("completed" or "failed")) throw new ArgumentOutOfRangeException(nameof(status));
        using var command = database.Command("UPDATE videos SET person_analysis_status=$status,person_analyzed_utc=$now WHERE id IN (SELECT video_id FROM sync_video_owners WHERE content_id=(SELECT content_id FROM videos WHERE id=$id) AND owner_key=(SELECT owner_key FROM sync_video_owners WHERE video_id=$id))");
        command.Parameters.AddWithValue("$status", status);command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("o"));command.Parameters.AddWithValue("$id",videoId);command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void SaveTranscript(long videoId, string modelVersion, string transcriptJson, VideoRecord? expected = null)
    {
        using var transaction = database.BeginTransaction();
        if (expected is not null && !IsCurrentContent(videoId, expected)) throw new InvalidOperationException("video_content_changed");
        using var command=database.Command("""
            INSERT INTO video_transcripts(video_id,model_version,transcript_json,created_utc) VALUES($video,$model,$json,$now)
            ON CONFLICT(video_id) DO UPDATE SET model_version=excluded.model_version,transcript_json=excluded.transcript_json,created_utc=excluded.created_utc
            """);
        command.Parameters.AddWithValue("$video",GetContentId(videoId)); command.Parameters.AddWithValue("$model",modelVersion);
        command.Parameters.AddWithValue("$json",transcriptJson); command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("o")); command.ExecuteNonQuery();
        transaction.Commit();
    }

    public string? GetTranscript(long videoId)
    {
        using var command=database.Command("SELECT transcript_json FROM video_transcripts WHERE video_id=$video");
        command.Parameters.AddWithValue("$video",GetContentId(videoId)); return command.ExecuteScalar() as string;
    }

    public (string ModelVersion, string Json)? GetTranscriptWithModel(long videoId)
    {
        using var command=database.Command("SELECT model_version,transcript_json FROM video_transcripts WHERE video_id=$video");
        command.Parameters.AddWithValue("$video",GetContentId(videoId));
        using var reader=command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    public AnalysisBillingRepository Billing { get; } = new(database);

    public void SaveSummary(long videoId,ICloudVisionAnalyzer analyzer,VideoRecord video,CloudVisionResult result, string? requestId = null)
    {
        Billing.Record(requestId ?? Guid.NewGuid().ToString("N"), analyzer, result.Usage);
        using var transaction = database.BeginTransaction();
        if (!IsCurrentContent(videoId, video)) return;
        using var command=database.Command("""
            INSERT INTO video_summaries(video_id,provider,model_version,prompt_version,source_file_size,source_modified_utc,summary,result_json,input_tokens,output_tokens,estimated_cost_yuan,created_utc)
            VALUES($video,$provider,$model,$prompt,$size,$modified,$summary,$json,$input,$output,$cost,$now)
            ON CONFLICT(video_id) DO UPDATE SET provider=excluded.provider,model_version=excluded.model_version,prompt_version=excluded.prompt_version,source_file_size=excluded.source_file_size,source_modified_utc=excluded.source_modified_utc,summary=excluded.summary,result_json=excluded.result_json,input_tokens=excluded.input_tokens,output_tokens=excluded.output_tokens,estimated_cost_yuan=excluded.estimated_cost_yuan,created_utc=excluded.created_utc
            """);command.Parameters.AddWithValue("$video",GetContentId(videoId));command.Parameters.AddWithValue("$provider",analyzer.Provider);command.Parameters.AddWithValue("$model",analyzer.ModelVersion);command.Parameters.AddWithValue("$prompt",analyzer.PromptVersion);command.Parameters.AddWithValue("$size",video.FileSize);command.Parameters.AddWithValue("$modified",video.ModifiedUtc.ToString("o"));command.Parameters.AddWithValue("$summary",result.Summary);command.Parameters.AddWithValue("$json",System.Text.Json.JsonSerializer.Serialize(result));command.Parameters.AddWithValue("$input",result.Usage?.InputTokens??0);command.Parameters.AddWithValue("$output",result.Usage?.OutputTokens??0);command.Parameters.AddWithValue("$cost",result.Usage?.EstimatedCostYuan??0);command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("o"));command.ExecuteNonQuery();
        var metadata = new VideoSearchMetadata(database);
        var concepts = VisualConceptCatalog.Parse(string.Join(" ", (result.Scenes ?? []).Concat(result.Activities ?? []).Concat(result.Objects ?? []).Concat(result.SearchTerms ?? []))).ConceptIds;
        foreach (var concept in concepts) metadata.SetConcept(videoId, concept, analyzer.Provider + ":" + analyzer.ModelVersion, "suggested", transaction);
        transaction.Commit();
    }
    public string? GetSummary(long videoId){using var command=database.Command("SELECT summary FROM video_summaries WHERE video_id=$video");command.Parameters.AddWithValue("$video",GetContentId(videoId));return command.ExecuteScalar() as string;}

    public bool IsCurrentContent(long videoId, VideoRecord expected)
    {
        var current = Get(videoId);
        if (current is null || current.FileSize != expected.FileSize) return false;
        if (current.Sha256 is { Length: 64 } && expected.Sha256 is { Length: 64 })
            return current.Sha256.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase);
        return current.ModifiedUtc == expected.ModifiedUtc;
    }

    public bool HasCurrentSummary(VideoRecord video)
    {
        if (!IsCurrentContent(video.Id, video)) return false;
        using var command = database.Command("""
            SELECT 1 FROM video_summaries WHERE video_id=$id AND trim(summary)<>''
              AND source_file_size=$size AND (source_modified_utc=$modified OR EXISTS(SELECT 1 FROM video_contents WHERE id=$id AND length(sha256)=64)) LIMIT 1
            """);
        command.Parameters.AddWithValue("$id", GetContentId(video.Id));
        command.Parameters.AddWithValue("$size", video.FileSize);
        command.Parameters.AddWithValue("$modified", video.ModifiedUtc.ToString("o"));
        return command.ExecuteScalar() is not null;
    }
}

public sealed class VideoIndexService(PicerDatabase database, VideoRepository repository)
{
    public IReadOnlyList<VideoRecord> ScanDirectory(long sourceId, string directoryPath, bool includeDescendants = true,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directoryPath)) return [];
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeDescendants,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };
        var result = new List<VideoRecord>();
        foreach (var path in Directory.EnumerateFiles(directoryPath, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!VideoFormats.IsSupported(path)) continue;
            try { result.Add(repository.Upsert(sourceId, path, cancellationToken)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    public int ScanLocalSources(CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var source in database.GetSources())
        {
            if (!Directory.Exists(source.Path)) continue;
            count += ScanDirectory(source.Id, source.Path, true, cancellationToken).Count;
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
