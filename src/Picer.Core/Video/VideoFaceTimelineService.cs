using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Vision;

namespace Picer.Core.Video;

public sealed record VideoFaceTimelineRecord(double TimestampSeconds, string KeyframePath, NormalizedFaceBox Box,
    float Confidence, long? SuggestedPersonId, string? SuggestedPersonName, double? MatchConfidence);

public sealed class VideoFaceTimelineService(PicerDatabase database, IImageFaceAnalyzer analyzer, VisionAnalysisRepository photoFaces)
{
    public async Task<int> AnalyzeAsync(VideoRecord video, CancellationToken cancellationToken=default)
    {
        var known=photoFaces.GetEmbeddingsForMatching().Where(item=>item.PersonId is not null&&item.AssignmentState=="confirmed").ToArray();
        using var select=database.Command("SELECT id,timestamp_seconds,path FROM video_keyframes WHERE video_id=$video ORDER BY timestamp_seconds");
        select.Parameters.AddWithValue("$video",video.Id); using var reader=select.ExecuteReader();
        var frames=new List<(long Id,double Time,string Path)>();while(reader.Read())frames.Add((reader.GetInt64(0),reader.GetDouble(1),reader.GetString(2)));
        var total=0;
        foreach(var frame in frames){cancellationToken.ThrowIfCancellationRequested();if(!File.Exists(frame.Path))continue;
            var info=new FileInfo(frame.Path);var result=await analyzer.AnalyzeAsync(new ImageRecord{Path=frame.Path,FileSize=info.Length,ModifiedUtc=info.LastWriteTimeUtc},cancellationToken);
            using var transaction=database.BeginTransaction();using(var clear=database.Command("DELETE FROM video_detected_faces WHERE keyframe_id=$frame")){clear.Transaction=transaction;clear.Parameters.AddWithValue("$frame",frame.Id);clear.ExecuteNonQuery();}
            for(var ordinal=0;ordinal<result.Faces.Count;ordinal++){var face=result.Faces[ordinal];var match=BestMatch(face.Embedding,known);
                using var insert=database.Command("""
                    INSERT INTO video_detected_faces(video_id,keyframe_id,timestamp_seconds,ordinal,box_x,box_y,box_width,box_height,confidence,embedding,suggested_person_id,match_confidence,created_utc)
                    VALUES($video,$frame,$time,$ordinal,$x,$y,$w,$h,$confidence,$embedding,$person,$match,$now)
                    """);insert.Transaction=transaction;insert.Parameters.AddWithValue("$video",video.Id);insert.Parameters.AddWithValue("$frame",frame.Id);insert.Parameters.AddWithValue("$time",frame.Time);insert.Parameters.AddWithValue("$ordinal",ordinal);
                insert.Parameters.AddWithValue("$x",face.Box.X);insert.Parameters.AddWithValue("$y",face.Box.Y);insert.Parameters.AddWithValue("$w",face.Box.Width);insert.Parameters.AddWithValue("$h",face.Box.Height);insert.Parameters.AddWithValue("$confidence",face.Confidence);
                insert.Parameters.AddWithValue("$embedding",face.Embedding is null?DBNull.Value:ToBytes(face.Embedding));insert.Parameters.AddWithValue("$person",match.PersonId is null?DBNull.Value:match.PersonId);insert.Parameters.AddWithValue("$match",match.Score is null?DBNull.Value:match.Score);insert.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("o"));insert.ExecuteNonQuery();total++;}
            transaction.Commit();}
        return total;
    }

    public IReadOnlyList<VideoFaceTimelineRecord> GetTimeline(long videoId)
    {
        using var command=database.Command("""
            SELECT vf.timestamp_seconds,k.path,vf.box_x,vf.box_y,vf.box_width,vf.box_height,vf.confidence,vf.suggested_person_id,p.display_name,vf.match_confidence
            FROM video_detected_faces vf JOIN video_keyframes k ON k.id=vf.keyframe_id LEFT JOIN people p ON p.id=vf.suggested_person_id
            WHERE vf.video_id=$video ORDER BY vf.timestamp_seconds,vf.ordinal
            """);command.Parameters.AddWithValue("$video",videoId);using var reader=command.ExecuteReader();var result=new List<VideoFaceTimelineRecord>();
        while(reader.Read())result.Add(new(reader.GetDouble(0),reader.GetString(1),new(reader.GetFloat(2),reader.GetFloat(3),reader.GetFloat(4),reader.GetFloat(5)),reader.GetFloat(6),reader.IsDBNull(7)?null:reader.GetInt64(7),reader.IsDBNull(8)?null:reader.GetString(8),reader.IsDBNull(9)?null:reader.GetDouble(9)));return result;
    }

    private static (long? PersonId,double? Score) BestMatch(IReadOnlyList<float>? embedding,IReadOnlyList<FaceEmbeddingRecord> known){if(embedding is null)return(null,null);var best=known.Select(item=>(item.PersonId,Score:Cosine(embedding,item.Embedding))).OrderByDescending(item=>item.Score).FirstOrDefault();return best.Score>=.55?(best.PersonId,best.Score):(null,null);}
    private static double Cosine(IReadOnlyList<float> a,IReadOnlyList<float> b){if(a.Count!=b.Count)return-1;double sum=0,aa=0,bb=0;for(var i=0;i<a.Count;i++){sum+=a[i]*b[i];aa+=a[i]*a[i];bb+=b[i]*b[i];}return aa==0||bb==0?-1:sum/Math.Sqrt(aa*bb);}
    private static byte[] ToBytes(IReadOnlyList<float> values){var bytes=new byte[values.Count*4];for(var i=0;i<values.Count;i++)BitConverter.TryWriteBytes(bytes.AsSpan(i*4,4),values[i]);return bytes;}
}
