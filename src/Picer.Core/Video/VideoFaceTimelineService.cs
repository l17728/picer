using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Vision;

namespace Picer.Core.Video;

public sealed record VideoFaceTimelineRecord(double TimestampSeconds, string KeyframePath, NormalizedFaceBox Box,
    float Confidence, long FaceId, long? SuggestedPersonId, string? SuggestedPersonName, double? MatchConfidence,
    long? AssignedPersonId, string? AssignedPersonName, string? AssignmentState)
{
    public string ModelVersion { get; init; } = "legacy-video";
    public long? EffectivePersonId => AssignedPersonId ?? SuggestedPersonId;
    public string? EffectivePersonName => AssignedPersonName ?? SuggestedPersonName;
    public bool IsSuggested => AssignmentState is null && SuggestedPersonId is not null;
}
public sealed record VideoFaceBackfillSource(long FaceId, long VideoId, long KeyframeId, double TimestampSeconds,
    string KeyframePath, int Ordinal, NormalizedFaceBox Box, float Confidence);

public sealed class VideoFaceTimelineService(PicerDatabase database, IImageFaceAnalyzer analyzer, VisionAnalysisRepository photoFaces, Picer.Core.People.PeopleService? people = null, bool includeAllModels = false)
{
    public string PreferredModelVersion => analyzer.ModelVersion;

    public VideoFaceTimelineService WithDatabase(PicerDatabase connection) =>
        new(connection, analyzer, new VisionAnalysisRepository(connection), new Picer.Core.People.PeopleService(connection), includeAllModels);

    public IReadOnlyList<VideoFaceBackfillSource> GetFacesForBackfill(string modelVersion)
    {
        using var command=database.Command("""
            SELECT vf.id,(SELECT MIN(reference_id) FROM video_effective_faces WHERE id=vf.id),vf.keyframe_id,vf.timestamp_seconds,k.path,vf.ordinal,vf.box_x,vf.box_y,vf.box_width,vf.box_height,vf.confidence
            FROM video_detected_faces vf JOIN video_keyframes k ON k.id=vf.keyframe_id
            WHERE vf.model_version=$model AND EXISTS(SELECT 1 FROM video_effective_faces owned WHERE owned.id=vf.id) ORDER BY vf.video_id,vf.timestamp_seconds,vf.ordinal
            """); command.Parameters.AddWithValue("$model",modelVersion); using var reader=command.ExecuteReader();
        var result=new List<VideoFaceBackfillSource>();
        while(reader.Read()) result.Add(new(reader.GetInt64(0),reader.GetInt64(1),reader.GetInt64(2),reader.GetDouble(3),reader.GetString(4),reader.GetInt32(5),new(reader.GetFloat(6),reader.GetFloat(7),reader.GetFloat(8),reader.GetFloat(9)),reader.GetFloat(10)));
        return result;
    }

    public int SaveBackfilledEmbeddings(string sourceModel, string targetModel, IReadOnlyList<(VideoFaceBackfillSource Source, float[] Embedding)> values)
    {
        using var transaction=database.BeginTransaction(); var saved=0;
        foreach(var value in values){
            using (var valid = database.Command("""
                SELECT 1 FROM video_effective_faces f WHERE f.id=$face AND f.reference_id=$video
                    AND f.keyframe_id=$frame AND f.model_version=$model AND f.ordinal=$ordinal
                    AND f.timestamp_seconds=$time
                """))
            {
                valid.Transaction=transaction;
                valid.Parameters.AddWithValue("$face",value.Source.FaceId);
                valid.Parameters.AddWithValue("$frame", value.Source.KeyframeId);
                valid.Parameters.AddWithValue("$video", value.Source.VideoId);
                valid.Parameters.AddWithValue("$model",sourceModel);
                valid.Parameters.AddWithValue("$ordinal",value.Source.Ordinal);
                valid.Parameters.AddWithValue("$time",value.Source.TimestampSeconds);
                if (valid.ExecuteScalar() is null) throw new InvalidOperationException("video_content_changed");
            }
            using var insert=database.Command("""
                INSERT INTO video_detected_faces(video_id,keyframe_id,timestamp_seconds,ordinal,owner_key,model_version,box_x,box_y,box_width,box_height,confidence,embedding,created_utc)
                VALUES($video,$frame,$time,$ordinal,$owner,$model,$x,$y,$w,$h,$confidence,$embedding,$now)
                ON CONFLICT(keyframe_id,model_version,ordinal,owner_key) DO UPDATE SET embedding=excluded.embedding,confidence=excluded.confidence
                RETURNING id
                """); insert.Transaction=transaction; insert.Parameters.AddWithValue("$video",new VideoContentRepository(database).GetContentId(value.Source.VideoId));insert.Parameters.AddWithValue("$frame",value.Source.KeyframeId);insert.Parameters.AddWithValue("$time",value.Source.TimestampSeconds);insert.Parameters.AddWithValue("$ordinal",value.Source.Ordinal);insert.Parameters.AddWithValue("$owner",database.GetVideoOwnerKey(value.Source.VideoId,transaction));insert.Parameters.AddWithValue("$model",targetModel);insert.Parameters.AddWithValue("$x",value.Source.Box.X);insert.Parameters.AddWithValue("$y",value.Source.Box.Y);insert.Parameters.AddWithValue("$w",value.Source.Box.Width);insert.Parameters.AddWithValue("$h",value.Source.Box.Height);insert.Parameters.AddWithValue("$confidence",value.Source.Confidence);insert.Parameters.Add("$embedding",Microsoft.Data.Sqlite.SqliteType.Blob).Value=ToBytes(value.Embedding);insert.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("o")); var targetId=Convert.ToInt64(insert.ExecuteScalar());
            using var copy=database.Command("""
                UPDATE video_detected_faces SET assigned_person_id=(SELECT assigned_person_id FROM video_detected_faces WHERE id=$source), assignment_state=(SELECT assignment_state FROM video_detected_faces WHERE id=$source), updated_utc=$now
                WHERE id=$target AND NOT EXISTS(SELECT 1 FROM video_detected_faces WHERE id=$target AND assignment_state IS NOT NULL)
                    AND (EXISTS(SELECT 1 FROM video_detected_faces WHERE id=$source AND assignment_state IS NOT NULL)
                        OR EXISTS(SELECT 1 FROM metadata_edits WHERE kind='video-face-state' AND entity_id=$source))
                """); copy.Transaction=transaction;copy.Parameters.AddWithValue("$source",value.Source.FaceId);copy.Parameters.AddWithValue("$target",targetId);copy.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("o"));
            if(copy.ExecuteNonQuery()>0) database.RecordVideoFaceManualState(targetId,transaction);
            database.ReplayVideoFaceManualFields(targetId,transaction);saved++; }
        transaction.Commit(); return saved;
    }
    public async Task<int> AnalyzeAsync(VideoRecord video,
        IProgress<(int Completed, int Total)>? progress = null, CancellationToken cancellationToken=default)
    {
        var matchers = new Dictionary<string, FaceSuggestionEngine.Matcher>(StringComparer.OrdinalIgnoreCase);
        using var select=database.Command("SELECT id,timestamp_seconds,path FROM video_keyframes WHERE video_id=$video ORDER BY timestamp_seconds");
        select.Parameters.AddWithValue("$video",new VideoContentRepository(database).GetContentId(video.Id)); using var reader=select.ExecuteReader();
        var frames=new List<(long Id,double Time,string Path)>();while(reader.Read())frames.Add((reader.GetInt64(0),reader.GetDouble(1),reader.GetString(2)));
        var total=0;
        var completed = 0;
        progress?.Report((0, frames.Count));
        foreach(var frame in frames){cancellationToken.ThrowIfCancellationRequested();if(!File.Exists(frame.Path)){progress?.Report((++completed,frames.Count));continue;}
            var info=new FileInfo(frame.Path);var result=await analyzer.AnalyzeAsync(new ImageRecord{Path=frame.Path,FileSize=info.Length,ModifiedUtc=info.LastWriteTimeUtc},cancellationToken);
            var modelVersion = string.IsNullOrWhiteSpace(result.ModelVersion) ? analyzer.ModelVersion : result.ModelVersion;
            if (!matchers.TryGetValue(modelVersion, out var matcher))
                matchers[modelVersion] = matcher = new FaceSuggestionEngine(photoFaces, people ?? new Picer.Core.People.PeopleService(database)).CreateMatcher(modelVersion, database.GetVideoOwnerKey(video.Id));
            using var transaction=database.BeginTransaction();
            if (!new VideoRepository(database).IsCurrentContent(video.Id, video)) throw new InvalidOperationException("video_content_changed");
            for(var ordinal=0;ordinal<result.Faces.Count;ordinal++){var face=result.Faces[ordinal];var match=matcher.Match(face.Embedding, face.Confidence, face.Box.Width * face.Box.Height);
                using var insert=database.Command("""
                    INSERT INTO video_detected_faces(video_id,keyframe_id,timestamp_seconds,ordinal,owner_key,model_version,box_x,box_y,box_width,box_height,confidence,embedding,suggested_person_id,match_confidence,created_utc)
                    VALUES($video,$frame,$time,$ordinal,$owner,$model,$x,$y,$w,$h,$confidence,$embedding,$person,$match,$now)
                    ON CONFLICT(keyframe_id,model_version,ordinal,owner_key) DO UPDATE SET timestamp_seconds=excluded.timestamp_seconds,
                      box_x=excluded.box_x,box_y=excluded.box_y,box_width=excluded.box_width,box_height=excluded.box_height,
                      confidence=excluded.confidence,embedding=excluded.embedding,suggested_person_id=excluded.suggested_person_id,
                      match_confidence=excluded.match_confidence,created_utc=excluded.created_utc
                    RETURNING id
                    """);insert.Transaction=transaction;insert.Parameters.AddWithValue("$video",new VideoContentRepository(database).GetContentId(video.Id));insert.Parameters.AddWithValue("$frame",frame.Id);insert.Parameters.AddWithValue("$time",frame.Time);insert.Parameters.AddWithValue("$ordinal",ordinal);
                insert.Parameters.AddWithValue("$owner",database.GetVideoOwnerKey(video.Id,transaction));insert.Parameters.AddWithValue("$model",modelVersion);insert.Parameters.AddWithValue("$x",face.Box.X);insert.Parameters.AddWithValue("$y",face.Box.Y);insert.Parameters.AddWithValue("$w",face.Box.Width);insert.Parameters.AddWithValue("$h",face.Box.Height);insert.Parameters.AddWithValue("$confidence",face.Confidence);
                insert.Parameters.AddWithValue("$embedding",face.Embedding is null?DBNull.Value:ToBytes(face.Embedding));insert.Parameters.AddWithValue("$person",match.PersonId is null?DBNull.Value:match.PersonId);insert.Parameters.AddWithValue("$match",match.Score is null?DBNull.Value:match.Score);insert.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("o"));
                var videoFaceId=Convert.ToInt64(insert.ExecuteScalar());
                using var suppressRejected=database.Command("""
                    UPDATE video_detected_faces SET suggested_person_id=NULL,match_confidence=NULL
                    WHERE id=$face AND suggested_person_id IS NOT NULL AND EXISTS(
                      SELECT 1 FROM video_face_person_rejections r
                      WHERE r.video_face_id=$face AND r.person_id=video_detected_faces.suggested_person_id)
                    """);
                suppressRejected.Transaction=transaction;suppressRejected.Parameters.AddWithValue("$face",videoFaceId);suppressRejected.ExecuteNonQuery();total++;}
            using(var removeStale=database.Command("""
                DELETE FROM video_detected_faces WHERE keyframe_id=$frame AND model_version=$model AND owner_key=$owner AND ordinal >= $count
                    AND assignment_state IS NULL
                    AND NOT EXISTS(SELECT 1 FROM video_face_person_rejections r WHERE r.video_face_id=video_detected_faces.id)
                    AND NOT EXISTS(SELECT 1 FROM metadata_edits e WHERE e.entity_id=video_detected_faces.id AND e.kind IN ('video-face-state','video-face-rejection'))
                """)){removeStale.Transaction=transaction;removeStale.Parameters.AddWithValue("$frame",frame.Id);removeStale.Parameters.AddWithValue("$model",modelVersion);removeStale.Parameters.AddWithValue("$count",result.Faces.Count);removeStale.Parameters.AddWithValue("$owner",database.GetVideoOwnerKey(video.Id,transaction));removeStale.ExecuteNonQuery();}
            transaction.Commit();progress?.Report((++completed,frames.Count));}
        return total;
    }

    public IReadOnlyList<VideoFaceTimelineRecord> GetTimeline(long videoId)
    {
        var preferred = GetTimelineForModel(videoId, analyzer.ModelVersion);
        if (includeAllModels || preferred.Count > 0) return preferred;

        // Prefer the configured/default model as a whole. Only when that model
        // has no displayable rows do we select one fallback space. Choosing a
        // single space avoids mixing distances, proposals, or duplicate crops.
        var fallbackModel = GetAvailableModels(videoId)
            .OrderBy(candidate => FallbackModelPriority(candidate.ModelVersion))
            .ThenByDescending(candidate => candidate.FaceCount)
            .ThenBy(candidate => candidate.ModelVersion, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.ModelVersion)
            .FirstOrDefault();
        return fallbackModel is null ? preferred : GetTimelineForModel(videoId, fallbackModel);
    }

    private IReadOnlyList<VideoFaceTimelineRecord> GetTimelineForModel(long videoId, string modelVersion)
    {
        using var command=database.Command("""
            SELECT vf.timestamp_seconds,k.path,vf.box_x,vf.box_y,vf.box_width,vf.box_height,vf.confidence,vf.id,
                   vf.suggested_person_id,sp.display_name,vf.match_confidence,vf.assigned_person_id,ap.display_name,vf.assignment_state,vf.model_version
            FROM video_detected_faces vf JOIN video_keyframes k ON k.id=vf.keyframe_id
            LEFT JOIN people sp ON sp.id=vf.suggested_person_id LEFT JOIN people ap ON ap.id=vf.assigned_person_id
            WHERE vf.video_id=$video AND vf.owner_key=$owner AND ($all=1 OR vf.model_version=$model OR
                (vf.model_version='legacy-video' AND NOT EXISTS(SELECT 1 FROM video_detected_faces current
                    WHERE current.keyframe_id=vf.keyframe_id AND current.owner_key=vf.owner_key AND current.ordinal=vf.ordinal AND current.model_version=$model)))
            ORDER BY vf.timestamp_seconds,vf.ordinal
            """);command.Parameters.AddWithValue("$video",new VideoContentRepository(database).GetContentId(videoId));command.Parameters.AddWithValue("$owner",database.GetVideoOwnerKey(videoId));command.Parameters.AddWithValue("$model",modelVersion);command.Parameters.AddWithValue("$all",includeAllModels ? 1 : 0);using var reader=command.ExecuteReader();var result=new List<VideoFaceTimelineRecord>();
        while(reader.Read())result.Add(new(reader.GetDouble(0),reader.GetString(1),new(reader.GetFloat(2),reader.GetFloat(3),reader.GetFloat(4),reader.GetFloat(5)),reader.GetFloat(6),reader.GetInt64(7),reader.IsDBNull(8)?null:reader.GetInt64(8),reader.IsDBNull(9)?null:reader.GetString(9),reader.IsDBNull(10)?null:reader.GetDouble(10),reader.IsDBNull(11)?null:reader.GetInt64(11),reader.IsDBNull(12)?null:reader.GetString(12),reader.IsDBNull(13)?null:reader.GetString(13)){ModelVersion=reader.GetString(14)});return result;
    }

    private IReadOnlyList<(string ModelVersion, int FaceCount)> GetAvailableModels(long videoId)
    {
        using var command = database.Command("""
            SELECT vf.model_version,COUNT(*)
            FROM video_detected_faces vf JOIN video_keyframes k ON k.id=vf.keyframe_id
            WHERE vf.video_id=$video AND vf.owner_key=$owner AND vf.model_version<>$preferred
            GROUP BY vf.model_version
            """);
        command.Parameters.AddWithValue("$video", new VideoContentRepository(database).GetContentId(videoId));command.Parameters.AddWithValue("$owner",database.GetVideoOwnerKey(videoId));
        command.Parameters.AddWithValue("$preferred", analyzer.ModelVersion);
        using var reader = command.ExecuteReader();
        var result = new List<(string ModelVersion, int FaceCount)>();
        while (reader.Read()) result.Add((reader.GetString(0), reader.GetInt32(1)));
        return result;
    }

    private static int FallbackModelPriority(string modelVersion)
    {
        if (modelVersion.Contains("insightface", StringComparison.OrdinalIgnoreCase)) return 0;
        if (modelVersion.Contains("adaface", StringComparison.OrdinalIgnoreCase)) return 1;
        if (modelVersion.Contains("arcface-paddle", StringComparison.OrdinalIgnoreCase)) return 2;
        if (modelVersion.Contains("facex-zoo", StringComparison.OrdinalIgnoreCase)) return 3;
        if (modelVersion.Contains("arcface", StringComparison.OrdinalIgnoreCase)) return 4;
        if (modelVersion.Contains("sface", StringComparison.OrdinalIgnoreCase)) return 5;
        if (modelVersion.Equals("legacy-video", StringComparison.OrdinalIgnoreCase)) return 6;
        return 10;
    }

    public IReadOnlyList<VideoFaceTimelineRecord> GetPersonFaces(long personId)
    {
        using var command=database.Command("""
            SELECT vf.timestamp_seconds,k.path,vf.box_x,vf.box_y,vf.box_width,vf.box_height,vf.confidence,vf.id,
                   vf.suggested_person_id,sp.display_name,vf.match_confidence,vf.assigned_person_id,ap.display_name,vf.assignment_state,vf.model_version
            FROM video_detected_faces vf JOIN video_keyframes k ON k.id=vf.keyframe_id
            LEFT JOIN people sp ON sp.id=vf.suggested_person_id LEFT JOIN people ap ON ap.id=vf.assigned_person_id
            WHERE vf.assigned_person_id=$person AND vf.assignment_state='confirmed'
                AND EXISTS(SELECT 1 FROM video_effective_faces owned WHERE owned.id=vf.id) AND ($all=1 OR vf.model_version=$model OR
                (vf.model_version='legacy-video' AND NOT EXISTS(SELECT 1 FROM video_detected_faces current
                    WHERE current.keyframe_id=vf.keyframe_id AND current.owner_key=vf.owner_key AND current.ordinal=vf.ordinal AND current.model_version=$model)))
            ORDER BY vf.timestamp_seconds,vf.id
            """);
        command.Parameters.AddWithValue("$person",personId);command.Parameters.AddWithValue("$model",analyzer.ModelVersion);command.Parameters.AddWithValue("$all",includeAllModels ? 1 : 0);using var reader=command.ExecuteReader();var result=new List<VideoFaceTimelineRecord>();
        while(reader.Read())result.Add(new(reader.GetDouble(0),reader.GetString(1),new(reader.GetFloat(2),reader.GetFloat(3),reader.GetFloat(4),reader.GetFloat(5)),reader.GetFloat(6),reader.GetInt64(7),reader.IsDBNull(8)?null:reader.GetInt64(8),reader.IsDBNull(9)?null:reader.GetString(9),reader.IsDBNull(10)?null:reader.GetDouble(10),reader.IsDBNull(11)?null:reader.GetInt64(11),reader.IsDBNull(12)?null:reader.GetString(12),reader.IsDBNull(13)?null:reader.GetString(13)){ModelVersion=reader.GetString(14)});return result;
    }

    public void Confirm(long videoFaceId,long personId)
    {
        using var transaction=database.BeginTransaction();
        database.RequireOwnedVideoFace(videoFaceId,transaction);
        using var command=database.Command("UPDATE video_detected_faces SET assigned_person_id=$person,assignment_state='confirmed',updated_utc=$now WHERE id=$face");
        command.Transaction=transaction;
        command.Parameters.AddWithValue("$person",personId);command.Parameters.AddWithValue("$face",videoFaceId);command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("o"));
        if(command.ExecuteNonQuery()==0)throw new KeyNotFoundException($"Video face {videoFaceId} was not found.");
        using var clear=database.Command("DELETE FROM video_face_person_rejections WHERE video_face_id=$face AND person_id=$person");
        clear.Transaction=transaction;clear.Parameters.AddWithValue("$face",videoFaceId);clear.Parameters.AddWithValue("$person",personId);
        if(clear.ExecuteNonQuery()>0) database.RecordVideoFacePersonRejection(videoFaceId,personId,false,transaction);
        database.RecordVideoFaceManualState(videoFaceId,transaction);transaction.Commit();
    }

    public void RejectSuggestion(long videoFaceId,long personId)
    {
        using var transaction=database.BeginTransaction();
        database.RequireOwnedVideoFace(videoFaceId,transaction);
        using(var reject=database.Command("INSERT OR IGNORE INTO video_face_person_rejections(video_face_id,person_id,created_utc) VALUES($face,$person,$now)")){reject.Transaction=transaction;reject.Parameters.AddWithValue("$face",videoFaceId);reject.Parameters.AddWithValue("$person",personId);reject.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("o"));reject.ExecuteNonQuery();}
        using(var clear=database.Command("UPDATE video_detected_faces SET suggested_person_id=NULL,match_confidence=NULL,updated_utc=$now WHERE id=$face")){clear.Transaction=transaction;clear.Parameters.AddWithValue("$face",videoFaceId);clear.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("o"));clear.ExecuteNonQuery();}
        database.RecordVideoFacePersonRejection(videoFaceId,personId,true,transaction);
        transaction.Commit();
    }

    public void MarkIgnored(long videoFaceId)
    {
        using var transaction=database.BeginTransaction();
        database.RequireOwnedVideoFace(videoFaceId,transaction);
        using var command=database.Command("UPDATE video_detected_faces SET assigned_person_id=NULL,assignment_state='ignored',updated_utc=$now WHERE id=$face");
        command.Transaction=transaction;
        command.Parameters.AddWithValue("$face",videoFaceId);command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("o"));command.ExecuteNonQuery();
        database.RecordVideoFaceManualState(videoFaceId,transaction);transaction.Commit();
    }

    public void ClearManualAssignment(long videoFaceId)
    {
        using var transaction=database.BeginTransaction();database.RequireOwnedVideoFace(videoFaceId,transaction);
        using var clear=database.Command("UPDATE video_detected_faces SET assigned_person_id=NULL,assignment_state=NULL,updated_utc=$now WHERE id=$face");
        clear.Transaction=transaction;clear.Parameters.AddWithValue("$face",videoFaceId);clear.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));clear.ExecuteNonQuery();
        database.RecordVideoFaceManualState(videoFaceId,transaction);transaction.Commit();
    }

    private static byte[] ToBytes(IReadOnlyList<float> values){var bytes=new byte[values.Count*4];for(var i=0;i<values.Count;i++)BitConverter.TryWriteBytes(bytes.AsSpan(i*4,4),values[i]);return bytes;}
}
