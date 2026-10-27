using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

public sealed record FaceManualState(long? PersonId,string State,string ModelVersion);

public sealed partial class PicerDatabase
{
    private static MetadataEdit NormalizeFaceManualEdit(MetadataEdit edit) => edit.Kind is "face-state" or "video-face-state"
        ? edit with { Value=JsonSerializer.Serialize(ParseFaceManualState(edit.Value)) }
        : edit.Kind is "face-rejection" or "video-face-rejection" ? edit with { Value=JsonSerializer.Serialize(ParseFacePersonRejection(edit.Value)) } : edit;

    public static FaceManualState ParseFaceManualState(string value)
    {
        try
        {
            using var document=JsonDocument.Parse(value);var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object || root.EnumerateObject().Count()!=3 ||
                !root.TryGetProperty("PersonId",out var person) || !root.TryGetProperty("State",out var state) ||
                !root.TryGetProperty("ModelVersion",out var model) || state.ValueKind!=JsonValueKind.String || model.ValueKind!=JsonValueKind.String)
                throw new ArgumentException("invalid_face_manual_state");
            var status=state.GetString();var version=model.GetString();long? personId=null;
            if(person.ValueKind!=JsonValueKind.Null)
            {
                if(person.ValueKind!=JsonValueKind.Number || !person.TryGetInt64(out var id) || id<=0) throw new ArgumentException("invalid_face_manual_state");
                personId=id;
            }
            if(status is not ("confirmed" or "ignored" or "unassigned") || (status=="confirmed")!=(personId is not null) ||
                string.IsNullOrWhiteSpace(version) || version.Length>256) throw new ArgumentException("invalid_face_manual_state");
            return new(personId,status,version);
        }
        catch(JsonException) { throw new ArgumentException("invalid_face_manual_state"); }
    }

    public void RecordFaceManualState(long faceId,SqliteTransaction transaction)
    {
        using var query=Command("""
            SELECT f.model_version,a.person_id,a.state,r.state FROM detected_faces f
            LEFT JOIN face_person_assignments a ON a.face_id=f.id LEFT JOIN face_review_states r ON r.face_id=f.id WHERE f.id=$id
            """);
        query.Transaction=transaction;query.Parameters.AddWithValue("$id",faceId);
        FaceManualState state;
        using(var row=query.ExecuteReader())
        {
            if(!row.Read()) throw new ArgumentException("face_missing");
            var ignored=!row.IsDBNull(3)&&row.GetString(3)=="ignored";
            var confirmed=!ignored&&!row.IsDBNull(2)&&row.GetString(2)=="confirmed";
            state=new(confirmed?row.GetInt64(1):null,ignored?"ignored":confirmed?"confirmed":"unassigned",row.GetString(0));
        }
        RecordMetadataEdit("face-state",faceId,"state",JsonSerializer.Serialize(state),transaction);
    }

    private MetadataEdit? GetFaceManualEdit(long faceId,SqliteTransaction? transaction)
    {
        var edit=FindMetadataEdit(new("face-state",faceId,"state","",DateTime.UtcNow.ToString("O"),Guid.NewGuid().ToString("N")),transaction);
        if(edit is null) return null;
        var state=ParseFaceManualState(edit.Value);
        using var query=Command("SELECT 1 FROM detected_faces WHERE id=$id AND model_version=$model AND ($person IS NULL OR EXISTS(SELECT 1 FROM people WHERE id=$person))");query.Transaction=transaction;
        query.Parameters.AddWithValue("$id",faceId);query.Parameters.AddWithValue("$model",state.ModelVersion);
        query.Parameters.AddWithValue("$person",(object?)state.PersonId??DBNull.Value);
        return query.ExecuteScalar() is null?null:edit;
    }

    internal bool HasFaceManualState(long faceId,SqliteTransaction? transaction) => GetFaceManualEdit(faceId,transaction) is not null;

    private void ApplyFaceManualState(MetadataEdit edit,SqliteTransaction transaction)
    {
        var state=ParseFaceManualState(edit.Value);
        using var command=Command(state.State switch
        {
            "confirmed"=>"""
                DELETE FROM face_review_states WHERE face_id=$face;
                INSERT INTO face_person_assignments(face_id,person_id,state,confidence,assigned_by,updated_utc)
                VALUES($face,$person,'confirmed',NULL,'user',$now)
                ON CONFLICT(face_id) DO UPDATE SET person_id=excluded.person_id,state='confirmed',confidence=NULL,assigned_by='user',updated_utc=excluded.updated_utc
                WHERE face_person_assignments.person_id IS NOT excluded.person_id OR face_person_assignments.state IS NOT 'confirmed'
                    OR face_person_assignments.assigned_by IS NOT 'user' OR face_person_assignments.confidence IS NOT NULL;
                """,
            "ignored"=>"""
                DELETE FROM face_person_assignments WHERE face_id=$face;
                DELETE FROM face_candidate_members WHERE face_id=$face;
                INSERT INTO face_review_states(face_id,state,updated_utc) VALUES($face,'ignored',$now)
                ON CONFLICT(face_id) DO UPDATE SET state='ignored',updated_utc=excluded.updated_utc WHERE face_review_states.state IS NOT 'ignored';
                """,
            _=>"""
                DELETE FROM face_review_states WHERE face_id=$face;
                DELETE FROM face_person_assignments WHERE face_id=$face AND (state='confirmed' OR assigned_by='user');
                """
        });
        command.Transaction=transaction;command.Parameters.AddWithValue("$face",edit.EntityId);
        command.Parameters.AddWithValue("$person",(object?)state.PersonId??DBNull.Value);command.Parameters.AddWithValue("$now",edit.UpdatedUtc);
        command.ExecuteNonQuery();
    }
}
