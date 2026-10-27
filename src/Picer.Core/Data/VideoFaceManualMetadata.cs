using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    public void RecordVideoFaceManualState(long faceId,SqliteTransaction transaction)
    {
        RequireOwnedVideoFace(faceId,transaction);
        using var query=Command("SELECT model_version,assigned_person_id,assignment_state FROM video_detected_faces WHERE id=$id");
        query.Transaction=transaction;query.Parameters.AddWithValue("$id",faceId);
        FaceManualState state;
        using(var row=query.ExecuteReader())
        {
            if(!row.Read()) throw new ArgumentException("video_face_missing");
            var status=row.IsDBNull(2)?"unassigned":row.GetString(2);
            state=new(status=="confirmed"&&!row.IsDBNull(1)?row.GetInt64(1):null,
                status=="ignored"?"ignored":status=="confirmed"&&!row.IsDBNull(1)?"confirmed":"unassigned",row.GetString(0));
        }
        RecordMetadataEdit("video-face-state",faceId,"state",JsonSerializer.Serialize(state),transaction);
    }

    public void RecordVideoFacePersonRejection(long faceId,long personId,bool rejected,SqliteTransaction transaction)
    {
        RequireOwnedVideoFace(faceId,transaction);
        using var face=Command("SELECT model_version FROM video_detected_faces WHERE id=$id");
        face.Transaction=transaction;face.Parameters.AddWithValue("$id",faceId);
        var model=face.ExecuteScalar() as string??throw new ArgumentException("video_face_missing");
        RecordMetadataEdit("video-face-rejection",faceId,personId.ToString(CultureInfo.InvariantCulture),
            JsonSerializer.Serialize(new FacePersonRejection(rejected,model)),transaction);
    }

    private void ApplyVideoFaceManualState(MetadataEdit edit,SqliteTransaction transaction)
    {
        var state=ParseFaceManualState(edit.Value);
        using var update=Command("""
            UPDATE video_detected_faces SET assigned_person_id=$person,assignment_state=$state,updated_utc=$utc
            WHERE id=$id AND (assigned_person_id IS NOT $person OR assignment_state IS NOT $state);
            """);
        update.Transaction=transaction;update.Parameters.AddWithValue("$id",edit.EntityId);
        update.Parameters.AddWithValue("$person",(object?)state.PersonId??DBNull.Value);
        update.Parameters.AddWithValue("$state",state.State=="unassigned"?DBNull.Value:state.State);
        update.Parameters.AddWithValue("$utc",edit.UpdatedUtc);update.ExecuteNonQuery();
    }

    private void ApplyVideoFacePersonRejection(MetadataEdit edit,SqliteTransaction transaction)
    {
        var rejection=ParseFacePersonRejection(edit.Value);
        using var command=Command(rejection.Rejected?"""
            INSERT OR IGNORE INTO video_face_person_rejections(video_face_id,person_id,created_utc) VALUES($face,$person,$utc);
            UPDATE video_detected_faces SET suggested_person_id=NULL,match_confidence=NULL
                WHERE id=$face AND suggested_person_id=$person;
            """:"DELETE FROM video_face_person_rejections WHERE video_face_id=$face AND person_id=$person");
        command.Transaction=transaction;command.Parameters.AddWithValue("$face",edit.EntityId);
        command.Parameters.AddWithValue("$person",long.Parse(edit.Field,CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$utc",edit.UpdatedUtc);command.ExecuteNonQuery();
    }

    internal void ReplayVideoFaceManualFields(long faceId,SqliteTransaction transaction)
    {
        var edits=new List<MetadataEdit>();
        using(var query=Command("""
            SELECT kind,field,value,updated_utc,operation_id,server_version,base_version
            FROM metadata_edits WHERE kind IN ('video-face-state','video-face-rejection') AND entity_id=$id
            """))
        {
            query.Transaction=transaction;query.Parameters.AddWithValue("$id",faceId);
            using var rows=query.ExecuteReader();
            while(rows.Read()) edits.Add(new(rows.GetString(0),faceId,rows.GetString(1),rows.GetString(2),rows.GetString(3),rows.GetString(4))
                { ServerVersion=rows.GetInt64(5),BaseVersion=rows.GetInt64(6) });
        }
        foreach(var edit in edits) ApplyMetadataEdit(edit,transaction);
    }
}
