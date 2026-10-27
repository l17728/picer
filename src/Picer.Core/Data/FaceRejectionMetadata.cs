using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

public sealed record FacePersonRejection(bool Rejected,string ModelVersion);

public sealed partial class PicerDatabase
{
    public static FacePersonRejection ParseFacePersonRejection(string value)
    {
        try
        {
            using var document=JsonDocument.Parse(value);var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object || root.EnumerateObject().Count()!=2 ||
                !root.TryGetProperty("Rejected",out var rejected) || rejected.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !root.TryGetProperty("ModelVersion",out var model) || model.ValueKind!=JsonValueKind.String ||
                string.IsNullOrWhiteSpace(model.GetString()) || model.GetString()!.Length>256)
                throw new ArgumentException("invalid_face_rejection");
            return new(rejected.GetBoolean(),model.GetString()!);
        }
        catch(JsonException) { throw new ArgumentException("invalid_face_rejection"); }
    }

    public void RecordFacePersonRejection(long faceId,long personId,bool rejected,SqliteTransaction transaction)
    {
        using var face=Command("SELECT model_version FROM detected_faces WHERE id=$id");face.Transaction=transaction;
        face.Parameters.AddWithValue("$id",faceId);
        var model=face.ExecuteScalar() as string??throw new ArgumentException("face_missing");
        RecordMetadataEdit("face-rejection",faceId,personId.ToString(CultureInfo.InvariantCulture),
            JsonSerializer.Serialize(new FacePersonRejection(rejected,model)),transaction);
    }

    private void ApplyFacePersonRejection(MetadataEdit edit,SqliteTransaction transaction)
    {
        var value=ParseFacePersonRejection(edit.Value);
        using var command=Command(value.Rejected?"""
            INSERT OR IGNORE INTO face_person_rejections(face_id,person_id,created_utc) VALUES($face,$person,$utc);
            DELETE FROM face_person_assignments WHERE face_id=$face AND person_id=$person AND state='suggested';
            """:"DELETE FROM face_person_rejections WHERE face_id=$face AND person_id=$person");
        command.Transaction=transaction;command.Parameters.AddWithValue("$face",edit.EntityId);
        command.Parameters.AddWithValue("$person",long.Parse(edit.Field,CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$utc",edit.UpdatedUtc);command.ExecuteNonQuery();
    }
}
