using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using System.Text.Json;

namespace Picer.Core.Vision;

/// <summary>Re-detection preserves identities only for unambiguous spatial matches in the same model.</summary>
public static class FaceDetectionReconciler
{
    private sealed record Existing(long Id, int Ordinal, NormalizedFaceBox Box, bool Reviewed);

    public static void Save(PicerDatabase database, long imageId, string modelVersion,
        IReadOnlyList<FaceDetection> faces, SqliteTransaction transaction)
    {
        if (string.IsNullOrWhiteSpace(modelVersion)) throw new ArgumentException("face_model_required", nameof(modelVersion));
        foreach (var face in faces)
        {
            var box = face.Box;
            if (!float.IsFinite(box.X) || !float.IsFinite(box.Y) || !float.IsFinite(box.Width) || !float.IsFinite(box.Height) ||
                box.X < 0 || box.Y < 0 || box.Width <= 0 || box.Height <= 0 || box.X + box.Width > 1.001f || box.Y + box.Height > 1.001f ||
                !float.IsFinite(face.Confidence) || face.Confidence < 0 || face.Confidence > 1 ||
                face.Embedding is { } embedding && (embedding.Count == 0 || embedding.Count > 4096 || embedding.Any(value => !float.IsFinite(value))))
                throw new InvalidDataException("invalid_face_detection");
        }
        var existing = new List<Existing>();
        using (var read = database.Command("""
            SELECT f.id,f.ordinal,f.box_x,f.box_y,f.box_width,f.box_height,
                EXISTS(SELECT 1 FROM face_person_assignments a WHERE a.face_id=f.id AND a.assigned_by='user')
                OR EXISTS(SELECT 1 FROM face_person_rejections r WHERE r.face_id=f.id)
                OR EXISTS(SELECT 1 FROM face_review_states s WHERE s.face_id=f.id)
                OR EXISTS(SELECT 1 FROM face_corrections c WHERE c.face_id=f.id)
            FROM detected_faces f WHERE f.image_id=$image AND f.model_version=$model
            """))
        {
            read.Transaction = transaction;
            read.Parameters.AddWithValue("$image", imageId); read.Parameters.AddWithValue("$model", modelVersion);
            using var reader = read.ExecuteReader();
            while (reader.Read()) existing.Add(new(reader.GetInt64(0), reader.GetInt32(1),
                new(reader.GetFloat(2), reader.GetFloat(3), reader.GetFloat(4), reader.GetFloat(5)), reader.GetBoolean(6)));
        }
        var matches = faces.Select(face => existing.Where(old => Overlap(face.Box, old.Box) >= .75).ToArray()).ToArray();
        var used = new HashSet<long>();
        var nextOrdinal = existing.Count == 0 ? 0 : existing.Max(face => face.Ordinal) + 1;
        for (var index = 0; index < faces.Count; index++)
        {
            var face = faces[index];
            var match = matches[index].Length == 1 && matches.Count(candidates => candidates.Any(old => old.Id == matches[index][0].Id)) == 1
                ? matches[index][0] : null;
            if (match is not null) used.Add(match.Id);
            using var write = database.Command(match is null ? """
                INSERT INTO detected_faces(image_id,model_version,ordinal,box_x,box_y,box_width,box_height,confidence,landmarks_json,embedding,created_utc)
                VALUES($image,$model,$ordinal,$x,$y,$w,$h,$confidence,$landmarks,$embedding,$created)
                """ : """
                UPDATE detected_faces SET box_x=$x,box_y=$y,box_width=$w,box_height=$h,confidence=$confidence,
                    landmarks_json=$landmarks,embedding=$embedding,is_archived=0 WHERE id=$id
                """);
            write.Transaction = transaction;
            write.Parameters.AddWithValue("$id", match?.Id ?? 0);
            write.Parameters.AddWithValue("$image", imageId); write.Parameters.AddWithValue("$model", modelVersion);
            write.Parameters.AddWithValue("$ordinal", match?.Ordinal ?? nextOrdinal++);
            write.Parameters.AddWithValue("$x", face.Box.X); write.Parameters.AddWithValue("$y", face.Box.Y);
            write.Parameters.AddWithValue("$w", face.Box.Width); write.Parameters.AddWithValue("$h", face.Box.Height);
            write.Parameters.AddWithValue("$confidence", face.Confidence);
            write.Parameters.AddWithValue("$landmarks", face.Landmarks is null ? DBNull.Value : JsonSerializer.Serialize(face.Landmarks));
            byte[]? bytes = null;
            if (face.Embedding is not null)
            {
                var vector = face.Embedding.ToArray(); bytes = new byte[vector.Length * sizeof(float)];
                Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
            }
            write.Parameters.Add("$embedding", SqliteType.Blob).Value = (object?)bytes ?? DBNull.Value;
            write.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("o"));
            write.ExecuteNonQuery();
        }
        // Unmatched reviewed faces remain evidence; never transfer their decisions to a new face.
        foreach (var obsolete in existing.Where(old => !old.Reviewed && !used.Contains(old.Id)))
        {
            using var delete = database.Command("DELETE FROM detected_faces WHERE id=$id");
            delete.Transaction = transaction; delete.Parameters.AddWithValue("$id", obsolete.Id); delete.ExecuteNonQuery();
        }
        foreach (var archived in existing.Where(old => old.Reviewed && !used.Contains(old.Id)))
        {
            using var archive = database.Command("UPDATE detected_faces SET is_archived=1 WHERE id=$id");
            archive.Transaction = transaction; archive.Parameters.AddWithValue("$id", archived.Id); archive.ExecuteNonQuery();
        }
    }

    private static double Overlap(NormalizedFaceBox left, NormalizedFaceBox right)
    {
        var area = Math.Max(0, Math.Min(left.X + left.Width, right.X + right.Width) - Math.Max(left.X, right.X)) *
            Math.Max(0, Math.Min(left.Y + left.Height, right.Y + right.Height) - Math.Max(left.Y, right.Y));
        return area / (left.Width * left.Height + right.Width * right.Height - area);
    }
}
