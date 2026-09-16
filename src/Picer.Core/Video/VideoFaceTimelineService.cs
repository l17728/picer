using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Vision;

namespace Picer.Core.Video;

public sealed record VideoFaceTimelineRecord(double TimestampSeconds, string KeyframePath, NormalizedFaceBox Box,
    float Confidence, long FaceId, long? SuggestedPersonId, string? SuggestedPersonName, double? MatchConfidence,
    long? AssignedPersonId, string? AssignedPersonName, string? AssignmentState)
{
    public long? EffectivePersonId => AssignedPersonId ?? SuggestedPersonId;
    public string? EffectivePersonName => AssignedPersonName ?? SuggestedPersonName;
    public bool IsSuggested => AssignmentState is null && SuggestedPersonId is not null;
}

public sealed class VideoFaceTimelineService(PicerDatabase database, IImageFaceAnalyzer analyzer, VisionAnalysisRepository photoFaces, Picer.Core.People.PeopleService? people = null)
{
    public async Task<int> AnalyzeAsync(VideoRecord video,
        IProgress<(int Completed, int Total)>? progress = null, CancellationToken cancellationToken=default)
    {
        var known=photoFaces.GetEmbeddingsForMatching().Where(item=>item.PersonId is not null&&item.AssignmentState=="confirmed").ToList();
        if(people is not null)known.AddRange(people.GetConfirmedVideoFaceEmbeddings().Select(face=>new FaceEmbeddingRecord(-face.VideoFaceId,0,analyzer.ModelVersion,face.PersonId,"confirmed",face.Embedding)));
        using var select=database.Command("SELECT id,timestamp_seconds,path FROM video_keyframes WHERE video_id=$video ORDER BY timestamp_seconds");
        select.Parameters.AddWithValue("$video",video.Id); using var reader=select.ExecuteReader();
        var frames=new List<(long Id,double Time,string Path)>();while(reader.Read())frames.Add((reader.GetInt64(0),reader.GetDouble(1),reader.GetString(2)));
        var total=0;
        var completed = 0;
        progress?.Report((0, frames.Count));
        foreach(var frame in frames){cancellationToken.ThrowIfCancellationRequested();if(!File.Exists(frame.Path)){progress?.Report((++completed,frames.Count));continue;}
            var info=new FileInfo(frame.Path);var result=await analyzer.AnalyzeAsync(new ImageRecord{Path=frame.Path,FileSize=info.Length,ModifiedUtc=info.LastWriteTimeUtc},cancellationToken);
            using var transaction=database.BeginTransaction();
            for(var ordinal=0;ordinal<result.Faces.Count;ordinal++){var face=result.Faces[ordinal];var match=BestMatch(face.Embedding,known);
                using var insert=database.Command("""
                    INSERT INTO video_detected_faces(video_id,keyframe_id,timestamp_seconds,ordinal,model_version,box_x,box_y,box_width,box_height,confidence,embedding,suggested_person_id,match_confidence,created_utc)
                    VALUES($video,$frame,$time,$ordinal,$model,$x,$y,$w,$h,$confidence,$embedding,$person,$match,$now)
                    ON CONFLICT(keyframe_id,model_version,ordinal) DO UPDATE SET timestamp_seconds=excluded.timestamp_seconds,
                      box_x=excluded.box_x,box_y=excluded.box_y,box_width=excluded.box_width,box_height=excluded.box_height,
                      confidence=excluded.confidence,embedding=excluded.embedding,suggested_person_id=excluded.suggested_person_id,
                      match_confidence=excluded.match_confidence,created_utc=excluded.created_utc
                    RETURNING id
                    """);insert.Transaction=transaction;insert.Parameters.AddWithValue("$video",video.Id);insert.Parameters.AddWithValue("$frame",frame.Id);insert.Parameters.AddWithValue("$time",frame.Time);insert.Parameters.AddWithValue("$ordinal",ordinal);
                insert.Parameters.AddWithValue("$model",analyzer.ModelVersion);insert.Parameters.AddWithValue("$x",face.Box.X);insert.Parameters.AddWithValue("$y",face.Box.Y);insert.Parameters.AddWithValue("$w",face.Box.Width);insert.Parameters.AddWithValue("$h",face.Box.Height);insert.Parameters.AddWithValue("$confidence",face.Confidence);
                insert.Parameters.AddWithValue("$embedding",face.Embedding is null?DBNull.Value:ToBytes(face.Embedding));insert.Parameters.AddWithValue("$person",match.PersonId is null?DBNull.Value:match.PersonId);insert.Parameters.AddWithValue("$match",match.Score is null?DBNull.Value:match.Score);insert.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("o"));
                var videoFaceId=Convert.ToInt64(insert.ExecuteScalar());
                using var suppressRejected=database.Command("""
                    UPDATE video_detected_faces SET suggested_person_id=NULL,match_confidence=NULL
                    WHERE id=$face AND suggested_person_id IS NOT NULL AND EXISTS(
                      SELECT 1 FROM video_face_person_rejections r
                      WHERE r.video_face_id=$face AND r.person_id=video_detected_faces.suggested_person_id)
                    """);
                suppressRejected.Transaction=transaction;suppressRejected.Parameters.AddWithValue("$face",videoFaceId);suppressRejected.ExecuteNonQuery();total++;}
            using(var removeStale=database.Command("DELETE FROM video_detected_faces WHERE keyframe_id=$frame AND model_version=$model AND ordinal >= $count")){removeStale.Transaction=transaction;removeStale.Parameters.AddWithValue("$frame",frame.Id);removeStale.Parameters.AddWithValue("$model",analyzer.ModelVersion);removeStale.Parameters.AddWithValue("$count",result.Faces.Count);removeStale.ExecuteNonQuery();}
            transaction.Commit();progress?.Report((++completed,frames.Count));}
        return total;
    }

    public IReadOnlyList<VideoFaceTimelineRecord> GetTimeline(long videoId)
    {
        using var command=database.Command("""
            SELECT vf.timestamp_seconds,k.path,vf.box_x,vf.box_y,vf.box_width,vf.box_height,vf.confidence,vf.id,
                   vf.suggested_person_id,sp.display_name,vf.match_confidence,vf.assigned_person_id,ap.display_name,vf.assignment_state
            FROM video_detected_faces vf JOIN video_keyframes k ON k.id=vf.keyframe_id
            LEFT JOIN people sp ON sp.id=vf.suggested_person_id LEFT JOIN people ap ON ap.id=vf.assigned_person_id
            WHERE vf.video_id=$video AND (vf.model_version=$model OR vf.model_version='legacy-video') ORDER BY vf.timestamp_seconds,vf.ordinal
            """);command.Parameters.AddWithValue("$video",videoId);command.Parameters.AddWithValue("$model",analyzer.ModelVersion);using var reader=command.ExecuteReader();var result=new List<VideoFaceTimelineRecord>();
        while(reader.Read())result.Add(new(reader.GetDouble(0),reader.GetString(1),new(reader.GetFloat(2),reader.GetFloat(3),reader.GetFloat(4),reader.GetFloat(5)),reader.GetFloat(6),reader.GetInt64(7),reader.IsDBNull(8)?null:reader.GetInt64(8),reader.IsDBNull(9)?null:reader.GetString(9),reader.IsDBNull(10)?null:reader.GetDouble(10),reader.IsDBNull(11)?null:reader.GetInt64(11),reader.IsDBNull(12)?null:reader.GetString(12),reader.IsDBNull(13)?null:reader.GetString(13)));return result;
    }

    public IReadOnlyList<VideoFaceTimelineRecord> GetPersonFaces(long personId)
    {
        using var command=database.Command("""
            SELECT vf.timestamp_seconds,k.path,vf.box_x,vf.box_y,vf.box_width,vf.box_height,vf.confidence,vf.id,
                   vf.suggested_person_id,sp.display_name,vf.match_confidence,vf.assigned_person_id,ap.display_name,vf.assignment_state
            FROM video_detected_faces vf JOIN video_keyframes k ON k.id=vf.keyframe_id
            LEFT JOIN people sp ON sp.id=vf.suggested_person_id LEFT JOIN people ap ON ap.id=vf.assigned_person_id
            WHERE vf.assigned_person_id=$person AND vf.assignment_state='confirmed' AND (vf.model_version=$model OR vf.model_version='legacy-video')
            ORDER BY vf.timestamp_seconds,vf.id
            """);
        command.Parameters.AddWithValue("$person",personId);command.Parameters.AddWithValue("$model",analyzer.ModelVersion);using var reader=command.ExecuteReader();var result=new List<VideoFaceTimelineRecord>();
        while(reader.Read())result.Add(new(reader.GetDouble(0),reader.GetString(1),new(reader.GetFloat(2),reader.GetFloat(3),reader.GetFloat(4),reader.GetFloat(5)),reader.GetFloat(6),reader.GetInt64(7),reader.IsDBNull(8)?null:reader.GetInt64(8),reader.IsDBNull(9)?null:reader.GetString(9),reader.IsDBNull(10)?null:reader.GetDouble(10),reader.IsDBNull(11)?null:reader.GetInt64(11),reader.IsDBNull(12)?null:reader.GetString(12),reader.IsDBNull(13)?null:reader.GetString(13)));return result;
    }

    public void Confirm(long videoFaceId,long personId)
    {
        using var command=database.Command("UPDATE video_detected_faces SET assigned_person_id=$person,assignment_state='confirmed',updated_utc=$now WHERE id=$face");
        command.Parameters.AddWithValue("$person",personId);command.Parameters.AddWithValue("$face",videoFaceId);command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("o"));
        if(command.ExecuteNonQuery()==0)throw new KeyNotFoundException($"Video face {videoFaceId} was not found.");
    }

    public void RejectSuggestion(long videoFaceId,long personId)
    {
        using var transaction=database.BeginTransaction();
        using(var reject=database.Command("INSERT OR IGNORE INTO video_face_person_rejections(video_face_id,person_id,created_utc) VALUES($face,$person,$now)")){reject.Transaction=transaction;reject.Parameters.AddWithValue("$face",videoFaceId);reject.Parameters.AddWithValue("$person",personId);reject.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("o"));reject.ExecuteNonQuery();}
        using(var clear=database.Command("UPDATE video_detected_faces SET suggested_person_id=NULL,match_confidence=NULL,updated_utc=$now WHERE id=$face")){clear.Transaction=transaction;clear.Parameters.AddWithValue("$face",videoFaceId);clear.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("o"));clear.ExecuteNonQuery();}
        transaction.Commit();
    }

    public void MarkIgnored(long videoFaceId)
    {
        using var command=database.Command("UPDATE video_detected_faces SET assigned_person_id=NULL,assignment_state='ignored',updated_utc=$now WHERE id=$face");
        command.Parameters.AddWithValue("$face",videoFaceId);command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("o"));command.ExecuteNonQuery();
    }

    private static (long? PersonId,double? Score) BestMatch(IReadOnlyList<float>? embedding,IReadOnlyList<FaceEmbeddingRecord> known){if(embedding is null)return(null,null);var best=known.Select(item=>(item.PersonId,Score:Cosine(embedding,item.Embedding))).OrderByDescending(item=>item.Score).FirstOrDefault();return best.Score>=.55?(best.PersonId,best.Score):(null,null);}
    private static double Cosine(IReadOnlyList<float> a,IReadOnlyList<float> b){if(a.Count!=b.Count)return-1;double sum=0,aa=0,bb=0;for(var i=0;i<a.Count;i++){sum+=a[i]*b[i];aa+=a[i]*a[i];bb+=b[i]*b[i];}return aa==0||bb==0?-1:sum/Math.Sqrt(aa*bb);}
    private static byte[] ToBytes(IReadOnlyList<float> values){var bytes=new byte[values.Count*4];for(var i=0;i<values.Count;i++)BitConverter.TryWriteBytes(bytes.AsSpan(i*4,4),values[i]);return bytes;}
}
