using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    private sealed record FaceOwnershipState(long Id,long Content,long Frame,int Ordinal,string Model,
        long? Assigned,long? Suggested,string? State,string? Updated,double X,double Y,double Width,double Height,double Time,string Rejections);

    private FaceOwnershipState? ReadFaceOwnershipState(long id,SqliteTransaction transaction)
    {
        using var query=Command("""
            SELECT id,video_id,keyframe_id,ordinal,model_version,assigned_person_id,suggested_person_id,assignment_state,updated_utc,
                box_x,box_y,box_width,box_height,timestamp_seconds,
                COALESCE((SELECT group_concat(person_id,',') FROM (SELECT person_id FROM video_face_person_rejections WHERE video_face_id=f.id ORDER BY person_id)),'')
            FROM video_detected_faces f WHERE id=$id
            """);query.Transaction=transaction;query.Parameters.AddWithValue("$id",id);
        using var row=query.ExecuteReader();
        return !row.Read()?null:new(row.GetInt64(0),row.GetInt64(1),row.GetInt64(2),row.GetInt32(3),row.GetString(4),
            row.IsDBNull(5)?null:row.GetInt64(5),row.IsDBNull(6)?null:row.GetInt64(6),row.IsDBNull(7)?null:row.GetString(7),row.IsDBNull(8)?null:row.GetString(8),
            row.GetDouble(9),row.GetDouble(10),row.GetDouble(11),row.GetDouble(12),row.GetDouble(13),row.GetString(14));
    }

    private FaceOwnershipState? ReadOwnedFaceState(FaceOwnershipState original,long reference,SqliteTransaction transaction)
    {
        using var query=Command("SELECT id FROM video_effective_faces WHERE reference_id=$video AND keyframe_id=$frame AND model_version=$model AND ordinal=$ordinal");
        query.Transaction=transaction;query.Parameters.AddWithValue("$video",reference);query.Parameters.AddWithValue("$frame",original.Frame);
        query.Parameters.AddWithValue("$model",original.Model);query.Parameters.AddWithValue("$ordinal",original.Ordinal);
        return query.ExecuteScalar() is long id?ReadFaceOwnershipState(id,transaction):null;
    }

    private static string? FaceOwnershipFingerprint(FaceOwnershipState? state) => state is null?null:
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state))))[..32].ToLowerInvariant();

    private string FaceOwnershipDisplay(FaceOwnershipState face,SqliteTransaction transaction)
    {
        if(face.State=="ignored") return "忽略此人脸";
        if(face.Assigned is not { } person) return "未确认人物";
        using var name=Command("SELECT display_name FROM people WHERE id=$id");name.Transaction=transaction;name.Parameters.AddWithValue("$id",person);
        return "确认人物："+(name.ExecuteScalar() as string??"已移除的人物");
    }

    private IReadOnlyList<VideoOwnershipReview> GetVideoFaceOwnershipReviews(SqliteTransaction transaction,string epoch,int limit)
    {
        var pending=new List<(long Face,string Operation)>();
        using(var query=Command("SELECT face_id,operation_id FROM video_face_ownership_reviews WHERE state='pending' ORDER BY face_id LIMIT $limit"))
        {
            query.Transaction=transaction;query.Parameters.AddWithValue("$limit",limit);
            using var rows=query.ExecuteReader();while(rows.Read()) pending.Add((rows.GetInt64(0),rows.GetString(1)));
        }
        var result=new List<VideoOwnershipReview>();
        foreach(var item in pending)
        {
            var face=ReadFaceOwnershipState(item.Face,transaction);if(face is null) continue;
            var references=new List<(long Id,string Path)>();
            using(var query=Command("SELECT id,path FROM videos WHERE id IN (SELECT MIN(video_id) FROM sync_video_owners WHERE content_id=$content AND owner_key NOT LIKE 'ambiguous-source:%' GROUP BY owner_key) ORDER BY id LIMIT 100"))
            {
                query.Transaction=transaction;query.Parameters.AddWithValue("$content",face.Content);
                using var rows=query.ExecuteReader();while(rows.Read()) references.Add((rows.GetInt64(0),rows.GetString(1)));
            }
            var candidates=references.Select(reference=>
            {
                var current=ReadOwnedFaceState(face,reference.Id,transaction);
                return new VideoOwnershipCandidate(reference.Id,reference.Path,current is null?0:1,FaceOwnershipFingerprint(current),
                    current is null?null:FaceOwnershipDisplay(current,transaction));
            }).ToArray();
            result.Add(new(item.Operation,"video-face",face.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                FaceOwnershipDisplay(face,transaction),candidates,$"视频人脸 · {face.Time:F1} 秒 · {face.Model} · 第 {face.Ordinal+1} 张",epoch));
        }
        return result;
    }

    private bool TryResolveVideoFaceOwnershipReview(VideoOwnershipResolution request,string resolution,SqliteTransaction transaction)
    {
        long faceId;
        using(var query=Command("SELECT face_id,state,resolution_json FROM video_face_ownership_reviews WHERE operation_id=$op"))
        {
            query.Transaction=transaction;query.Parameters.AddWithValue("$op",request.OperationId);using var row=query.ExecuteReader();
            if(!row.Read()) return false;
            if(row.GetString(1)!="pending")
            {
                if(!row.IsDBNull(2)&&row.GetString(2)==resolution) return true;
                throw new InvalidOperationException("video_face_ownership_review_changed");
            }
            faceId=row.GetInt64(0);
        }
        var face=ReadFaceOwnershipState(faceId,transaction)??throw new InvalidOperationException("video_face_missing");
        foreach(var choice in request.Choices)
        {
            using var reference=Command("SELECT 1 FROM videos WHERE id=$id AND content_id=$content");reference.Transaction=transaction;
            reference.Parameters.AddWithValue("$id",choice.VideoId);reference.Parameters.AddWithValue("$content",face.Content);
            if(reference.ExecuteScalar() is null) throw new InvalidOperationException("video_content_changed");
            var owner=GetVideoOwnerKey(choice.VideoId,transaction);var current=ReadOwnedFaceState(face,choice.VideoId,transaction);
            if(choice.Version!=(current is null?0:1) || choice.CurrentOperationId!=FaceOwnershipFingerprint(current))
                throw new InvalidOperationException("video_face_ownership_review_changed");
            if(current is not null && (face.X!=current.X || face.Y!=current.Y || face.Width!=current.Width || face.Height!=current.Height))
                throw new InvalidOperationException("video_face_review_geometry_changed");
            using var restore=Command("""
                INSERT INTO video_detected_faces(video_id,keyframe_id,timestamp_seconds,ordinal,owner_key,model_version,box_x,box_y,box_width,box_height,
                    confidence,embedding,suggested_person_id,match_confidence,assigned_person_id,assignment_state,updated_utc,created_utc)
                SELECT video_id,keyframe_id,timestamp_seconds,ordinal,$owner,model_version,box_x,box_y,box_width,box_height,
                    confidence,embedding,suggested_person_id,match_confidence,assigned_person_id,assignment_state,$now,created_utc
                FROM video_detected_faces WHERE id=$face
                ON CONFLICT(keyframe_id,model_version,ordinal,owner_key) DO UPDATE SET
                    suggested_person_id=excluded.suggested_person_id,match_confidence=excluded.match_confidence,
                    assigned_person_id=excluded.assigned_person_id,assignment_state=excluded.assignment_state,updated_utc=excluded.updated_utc
                RETURNING id;
                """);restore.Transaction=transaction;restore.Parameters.AddWithValue("$owner",owner);restore.Parameters.AddWithValue("$face",faceId);
            restore.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));var target=Convert.ToInt64(restore.ExecuteScalar());
            var rejectedPeople=new List<long>();
            using(var currentRejections=Command("SELECT DISTINCT person_id FROM video_face_person_rejections WHERE video_face_id IN ($target,$source)"))
            {
                currentRejections.Transaction=transaction;currentRejections.Parameters.AddWithValue("$target",target);currentRejections.Parameters.AddWithValue("$source",faceId);
                using var rows=currentRejections.ExecuteReader();while(rows.Read()) rejectedPeople.Add(rows.GetInt64(0));
            }
            using var rejection=Command("""
                DELETE FROM video_face_person_rejections WHERE video_face_id=$target;
                INSERT INTO video_face_person_rejections(video_face_id,person_id,created_utc)
                SELECT $target,person_id,created_utc FROM video_face_person_rejections WHERE video_face_id=$source
                    AND person_id IS NOT (SELECT assigned_person_id FROM video_detected_faces WHERE id=$target AND assignment_state='confirmed');
                """);rejection.Transaction=transaction;rejection.Parameters.AddWithValue("$target",target);rejection.Parameters.AddWithValue("$source",faceId);rejection.ExecuteNonQuery();
            RecordVideoFaceManualState(target,transaction);
            foreach(var person in rejectedPeople)
            {
                using var exists=Command("SELECT 1 FROM video_face_person_rejections WHERE video_face_id=$face AND person_id=$person");
                exists.Transaction=transaction;exists.Parameters.AddWithValue("$face",target);exists.Parameters.AddWithValue("$person",person);
                RecordVideoFacePersonRejection(target,person,exists.ExecuteScalar() is not null,transaction);
            }
        }
        using var finish=Command("UPDATE video_face_ownership_reviews SET state=$state,resolution_json=$resolution WHERE operation_id=$op");
        finish.Transaction=transaction;finish.Parameters.AddWithValue("$state",request.Discard?"discarded":"resolved");finish.Parameters.AddWithValue("$resolution",resolution);
        finish.Parameters.AddWithValue("$op",request.OperationId);finish.ExecuteNonQuery();return true;
    }
}
