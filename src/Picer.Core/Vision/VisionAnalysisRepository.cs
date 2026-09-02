using System.Text.Json;
using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.Core.Vision;

public sealed class VisionAnalysisRepository(PicerDatabase database)
{
    public bool IsCurrent(ImageRecord image, string analyzer, string modelVersion)
    {
        using var command = database.Command("""
            SELECT 1 FROM vision_analyses
            WHERE image_id = $image_id AND analyzer = $analyzer
              AND model_version = $model_version
              AND source_file_size = $file_size
              AND source_modified_utc = $modified_utc
              AND status = 'completed'
            """);
        command.Parameters.AddWithValue("$image_id", image.Id);
        command.Parameters.AddWithValue("$analyzer", analyzer);
        command.Parameters.AddWithValue("$model_version", modelVersion);
        command.Parameters.AddWithValue("$file_size", image.FileSize);
        command.Parameters.AddWithValue("$modified_utc", Format(image.ModifiedUtc));
        return command.ExecuteScalar() is not null;
    }

    public void SaveCompleted(ImageRecord image, IImageFaceAnalyzer analyzer, FaceAnalysisResult result)
    {
        using var transaction = database.BeginTransaction();
        DeleteFaces(image.Id, transaction);

        for (var ordinal = 0; ordinal < result.Faces.Count; ordinal++)
        {
            var face = result.Faces[ordinal];
            Validate(face);
            using var insert = database.Command("""
                INSERT INTO detected_faces
                    (image_id, model_version, ordinal, box_x, box_y, box_width, box_height,
                     confidence, landmarks_json, embedding, created_utc)
                VALUES
                    ($image_id, $model_version, $ordinal, $x, $y, $width, $height,
                     $confidence, $landmarks, $embedding, $created_utc)
                """);
            insert.Transaction = transaction;
            insert.Parameters.AddWithValue("$image_id", image.Id);
            insert.Parameters.AddWithValue("$model_version", analyzer.ModelVersion);
            insert.Parameters.AddWithValue("$ordinal", ordinal);
            insert.Parameters.AddWithValue("$x", face.Box.X);
            insert.Parameters.AddWithValue("$y", face.Box.Y);
            insert.Parameters.AddWithValue("$width", face.Box.Width);
            insert.Parameters.AddWithValue("$height", face.Box.Height);
            insert.Parameters.AddWithValue("$confidence", face.Confidence);
            insert.Parameters.AddWithValue("$landmarks", face.Landmarks is null
                ? DBNull.Value
                : JsonSerializer.Serialize(face.Landmarks));
            insert.Parameters.Add("$embedding", SqliteType.Blob).Value = face.Embedding is null
                ? DBNull.Value
                : ToBytes(face.Embedding);
            insert.Parameters.AddWithValue("$created_utc", Format(DateTime.UtcNow));
            insert.ExecuteNonQuery();
        }

        UpsertAnalysis(image, analyzer, "completed", result.Faces.Count, null, transaction);
        transaction.Commit();
    }

    public void SaveFailure(ImageRecord image, IImageFaceAnalyzer analyzer, string errorCode)
    {
        using var transaction = database.BeginTransaction();
        UpsertAnalysis(image, analyzer, "failed", 0, errorCode, transaction);
        transaction.Commit();
    }

    public VisionAnalysisRecord? Get(long imageId, string analyzer)
    {
        using var command = database.Command("""
            SELECT image_id, analyzer, model_version, source_file_size, source_modified_utc,
                   status, face_count, error_code, analyzed_utc
            FROM vision_analyses WHERE image_id = $image_id AND analyzer = $analyzer
            """);
        command.Parameters.AddWithValue("$image_id", imageId);
        command.Parameters.AddWithValue("$analyzer", analyzer);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new VisionAnalysisRecord(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
            Parse(reader.GetString(4)), reader.GetString(5), reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetString(7), Parse(reader.GetString(8)));
    }

    public int GetFaceCount(long imageId)
    {
        using var command = database.Command("SELECT COUNT(*) FROM detected_faces WHERE image_id = $image_id");
        command.Parameters.AddWithValue("$image_id", imageId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public IReadOnlyList<long> GetFaceIds(long imageId)
    {
        using var command = database.Command("SELECT id FROM detected_faces WHERE image_id = $image_id ORDER BY ordinal");
        command.Parameters.AddWithValue("$image_id", imageId);
        var ids = new List<long>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    public VisionAnalysisSummary GetSummary()
    {
        using var command = database.Command("""
            SELECT
                (SELECT COUNT(*) FROM images WHERE status='active'),
                (SELECT COUNT(*) FROM vision_analyses WHERE analyzer='face' AND status='completed'),
                (SELECT COUNT(*) FROM vision_analyses WHERE analyzer='face' AND status='failed'),
                (SELECT COUNT(*) FROM detected_faces)
            """);
        using var reader = command.ExecuteReader();
        reader.Read();
        return new VisionAnalysisSummary(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
    }

    public IReadOnlyList<UnassignedFaceRecord> GetUnassignedFaces(int limit = 500)
    {
        using var command = database.Command("""
            SELECT f.id, f.image_id, i.path, i.thumb_path, f.ordinal,
                   f.box_x, f.box_y, f.box_width, f.box_height, f.confidence,
                   CASE WHEN a.state='suggested' THEN a.person_id END,
                   CASE WHEN a.state='suggested' THEN p.display_name END
            FROM detected_faces f
            JOIN images i ON i.id=f.image_id AND i.status='active'
            LEFT JOIN face_person_assignments a ON a.face_id=f.id
            LEFT JOIN people p ON p.id=a.person_id
            LEFT JOIN face_review_states r ON r.face_id=f.id
            WHERE (a.face_id IS NULL OR a.state='suggested') AND r.face_id IS NULL
            ORDER BY f.created_utc DESC, f.id DESC
            LIMIT $limit
            """);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        var values = new List<UnassignedFaceRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            values.Add(new UnassignedFaceRecord(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4),
                new NormalizedFaceBox(reader.GetFloat(5), reader.GetFloat(6), reader.GetFloat(7), reader.GetFloat(8)),
                reader.GetFloat(9), reader.IsDBNull(10) ? null : reader.GetInt64(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }
        return values;
    }

    public IReadOnlyList<UnassignedFaceRecord> GetConfirmedFaces(long personId, int limit = 500)
    {
        using var command = database.Command("""
            SELECT f.id, f.image_id, i.path, i.thumb_path, f.ordinal,
                   f.box_x, f.box_y, f.box_width, f.box_height, f.confidence
            FROM face_person_assignments a
            JOIN detected_faces f ON f.id=a.face_id
            JOIN images i ON i.id=f.image_id AND i.status='active'
            WHERE a.person_id=$person AND a.state='confirmed'
            ORDER BY f.created_utc DESC, f.id DESC
            LIMIT $limit
            """);
        command.Parameters.AddWithValue("$person", personId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        var values = new List<UnassignedFaceRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) values.Add(new UnassignedFaceRecord(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4),
            new NormalizedFaceBox(reader.GetFloat(5), reader.GetFloat(6), reader.GetFloat(7), reader.GetFloat(8)),
            reader.GetFloat(9)));
        return values;
    }

    public IReadOnlyList<UnassignedFaceRecord> GetPersonFaces(long personId, int limit = 500)
    {
        using var command = database.Command("""
            SELECT f.id, f.image_id, i.path, i.thumb_path, f.ordinal,
                   f.box_x, f.box_y, f.box_width, f.box_height, f.confidence,
                   CASE WHEN a.state='suggested' THEN a.person_id END,
                   CASE WHEN a.state='suggested' THEN p.display_name END
            FROM face_person_assignments a
            JOIN detected_faces f ON f.id=a.face_id
            JOIN images i ON i.id=f.image_id AND i.status='active'
            JOIN people p ON p.id=a.person_id
            WHERE a.person_id=$person AND a.state IN ('confirmed','suggested')
            ORDER BY CASE a.state WHEN 'suggested' THEN 0 ELSE 1 END, f.created_utc DESC, f.id DESC
            LIMIT $limit
            """);
        command.Parameters.AddWithValue("$person", personId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        var values = new List<UnassignedFaceRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) values.Add(new UnassignedFaceRecord(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4),
            new NormalizedFaceBox(reader.GetFloat(5), reader.GetFloat(6), reader.GetFloat(7), reader.GetFloat(8)),
            reader.GetFloat(9), reader.IsDBNull(10) ? null : reader.GetInt64(10),
            reader.IsDBNull(11) ? null : reader.GetString(11)));
        return values;
    }

    public IReadOnlyList<UnassignedFaceRecord> GetIgnoredFaces(int limit = 500)
    {
        using var command = database.Command("""
            SELECT f.id, f.image_id, i.path, i.thumb_path, f.ordinal,
                   f.box_x, f.box_y, f.box_width, f.box_height, f.confidence
            FROM face_review_states r
            JOIN detected_faces f ON f.id=r.face_id
            JOIN images i ON i.id=f.image_id AND i.status='active'
            WHERE r.state='ignored'
            ORDER BY r.updated_utc DESC, f.id DESC
            LIMIT $limit
            """);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        var values = new List<UnassignedFaceRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) values.Add(new UnassignedFaceRecord(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4),
            new NormalizedFaceBox(reader.GetFloat(5), reader.GetFloat(6), reader.GetFloat(7), reader.GetFloat(8)),
            reader.GetFloat(9)));
        return values;
    }

    public IReadOnlyList<FaceEmbeddingRecord> GetEmbeddingsForMatching()
    {
        using var command = database.Command("""
            SELECT f.id, f.image_id, f.model_version, a.person_id, a.state, f.embedding
            FROM detected_faces f
            LEFT JOIN face_person_assignments a ON a.face_id=f.id
            LEFT JOIN face_review_states r ON r.face_id=f.id
            WHERE f.embedding IS NOT NULL AND r.face_id IS NULL
              AND (a.face_id IS NULL OR a.state IN ('suggested','confirmed'))
            ORDER BY f.id
            """);
        var values = new List<FaceEmbeddingRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) values.Add(new FaceEmbeddingRecord(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), FromBytes((byte[])reader[5])));
        return values;
    }

    public double? GetFaceMatchThreshold(string modelVersion)
    {
        using var command = database.Command("SELECT match_threshold FROM face_model_settings WHERE model_version=$version");
        command.Parameters.AddWithValue("$version", modelVersion);
        return command.ExecuteScalar() is { } value ? Convert.ToDouble(value) : null;
    }

    public void SaveFaceMatchThreshold(string modelVersion, double threshold, int samePairs, int differentPairs)
    {
        if (string.IsNullOrWhiteSpace(modelVersion)) throw new ArgumentException("Model version is required.", nameof(modelVersion));
        if (threshold is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(threshold));
        using var command = database.Command("""
            INSERT INTO face_model_settings(model_version,match_threshold,calibrated_same_pairs,calibrated_different_pairs,updated_utc)
            VALUES($version,$threshold,$same,$different,$now)
            ON CONFLICT(model_version) DO UPDATE SET match_threshold=excluded.match_threshold,
                calibrated_same_pairs=excluded.calibrated_same_pairs,
                calibrated_different_pairs=excluded.calibrated_different_pairs,updated_utc=excluded.updated_utc
            """);
        command.Parameters.AddWithValue("$version", modelVersion);
        command.Parameters.AddWithValue("$threshold", threshold);
        command.Parameters.AddWithValue("$same", Math.Max(0, samePairs));
        command.Parameters.AddWithValue("$different", Math.Max(0, differentPairs));
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        command.ExecuteNonQuery();
    }

    public void ReplaceCandidateClusters(string modelVersion, IReadOnlyList<CandidateClusterDraft> clusters)
    {
        using var transaction = database.BeginTransaction();
        using (var clear = database.Command("DELETE FROM face_candidate_clusters"))
        {
            clear.Transaction = transaction;
            clear.ExecuteNonQuery();
        }
        foreach (var cluster in clusters.Where(value => value.Members.Count >= 2))
        {
            using var insertCluster = database.Command("""
                INSERT INTO face_candidate_clusters(model_version, created_utc)
                VALUES($version,$now); SELECT last_insert_rowid();
                """);
            insertCluster.Transaction = transaction;
            insertCluster.Parameters.AddWithValue("$version", modelVersion);
            insertCluster.Parameters.AddWithValue("$now", Format(DateTime.UtcNow));
            var clusterId = (long)insertCluster.ExecuteScalar()!;
            foreach (var member in cluster.Members)
            {
                using var insertMember = database.Command("""
                    INSERT INTO face_candidate_members(cluster_id, face_id, similarity)
                    VALUES($cluster,$face,$similarity)
                    """);
                insertMember.Transaction = transaction;
                insertMember.Parameters.AddWithValue("$cluster", clusterId);
                insertMember.Parameters.AddWithValue("$face", member.FaceId);
                insertMember.Parameters.AddWithValue("$similarity", member.Similarity);
                insertMember.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }

    public IReadOnlyList<FaceCandidateClusterRecord> GetCandidateClusters()
    {
        using var command = database.Command("""
            SELECT c.id, c.model_version, f.id, f.image_id, i.path, i.thumb_path, f.ordinal,
                   f.box_x, f.box_y, f.box_width, f.box_height, f.confidence
            FROM face_candidate_clusters c
            JOIN face_candidate_members m ON m.cluster_id=c.id
            JOIN detected_faces f ON f.id=m.face_id
            JOIN images i ON i.id=f.image_id AND i.status='active'
            LEFT JOIN face_person_assignments a ON a.face_id=f.id
            LEFT JOIN face_review_states r ON r.face_id=f.id
            WHERE a.face_id IS NULL AND r.face_id IS NULL
            ORDER BY c.id, m.similarity DESC, f.id
            """);
        var groups = new Dictionary<long, (string Version, List<UnassignedFaceRecord> Faces)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var clusterId = reader.GetInt64(0);
            if (!groups.TryGetValue(clusterId, out var group))
            {
                group = (reader.GetString(1), []);
                groups.Add(clusterId, group);
            }
            group.Faces.Add(new UnassignedFaceRecord(
                reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt32(6),
                new NormalizedFaceBox(reader.GetFloat(7), reader.GetFloat(8), reader.GetFloat(9), reader.GetFloat(10)),
                reader.GetFloat(11)));
        }
        return groups.Where(pair => pair.Value.Faces.Count >= 2)
            .Select(pair => new FaceCandidateClusterRecord(pair.Key, pair.Value.Version, pair.Value.Faces)).ToArray();
    }

    public int GetIgnoredFaceCount()
    {
        using var command = database.Command("""
            SELECT COUNT(*) FROM face_review_states r
            JOIN detected_faces f ON f.id=r.face_id
            JOIN images i ON i.id=f.image_id AND i.status='active'
            WHERE r.state='ignored'
            """);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void RemoveFaceFromCandidateCluster(long clusterId, long faceId)
    {
        using var command = database.Command("DELETE FROM face_candidate_members WHERE cluster_id=$cluster AND face_id=$face");
        command.Parameters.AddWithValue("$cluster", clusterId);
        command.Parameters.AddWithValue("$face", faceId);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ImageFaceRecord> GetFaces(long imageId)
    {
        using var command = database.Command("""
            SELECT f.id, f.ordinal, f.box_x, f.box_y, f.box_width, f.box_height, f.confidence,
                   a.person_id, p.display_name, a.state
            FROM detected_faces f
            LEFT JOIN face_person_assignments a ON a.face_id=f.id
            LEFT JOIN people p ON p.id=a.person_id
            WHERE f.image_id=$image
            ORDER BY f.ordinal
            """);
        command.Parameters.AddWithValue("$image", imageId);
        var faces = new List<ImageFaceRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) faces.Add(new ImageFaceRecord(
            reader.GetInt64(0), reader.GetInt32(1),
            new NormalizedFaceBox(reader.GetFloat(2), reader.GetFloat(3), reader.GetFloat(4), reader.GetFloat(5)),
            reader.GetFloat(6), reader.IsDBNull(7) ? null : reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
        return faces;
    }

    private void DeleteFaces(long imageId, SqliteTransaction transaction)
    {
        using var command = database.Command("DELETE FROM detected_faces WHERE image_id = $image_id");
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$image_id", imageId);
        command.ExecuteNonQuery();
    }

    private void UpsertAnalysis(ImageRecord image, IImageFaceAnalyzer analyzer, string status,
        int faceCount, string? errorCode, SqliteTransaction transaction)
    {
        using var command = database.Command("""
            INSERT INTO vision_analyses
                (image_id, analyzer, model_version, source_file_size, source_modified_utc,
                 status, face_count, error_code, analyzed_utc)
            VALUES
                ($image_id, $analyzer, $model_version, $file_size, $modified_utc,
                 $status, $face_count, $error_code, $analyzed_utc)
            ON CONFLICT(image_id, analyzer) DO UPDATE SET
                model_version = excluded.model_version,
                source_file_size = excluded.source_file_size,
                source_modified_utc = excluded.source_modified_utc,
                status = excluded.status,
                face_count = excluded.face_count,
                error_code = excluded.error_code,
                analyzed_utc = excluded.analyzed_utc
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$image_id", image.Id);
        command.Parameters.AddWithValue("$analyzer", analyzer.AnalyzerName);
        command.Parameters.AddWithValue("$model_version", analyzer.ModelVersion);
        command.Parameters.AddWithValue("$file_size", image.FileSize);
        command.Parameters.AddWithValue("$modified_utc", Format(image.ModifiedUtc));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$face_count", faceCount);
        command.Parameters.AddWithValue("$error_code", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$analyzed_utc", Format(DateTime.UtcNow));
        command.ExecuteNonQuery();
    }

    private static void Validate(FaceDetection face)
    {
        var box = face.Box;
        if (box.X < 0 || box.Y < 0 || box.Width <= 0 || box.Height <= 0 ||
            box.X + box.Width > 1.0001f || box.Y + box.Height > 1.0001f)
            throw new ArgumentOutOfRangeException(nameof(face), "Face boxes must use normalized image coordinates.");
    }

    private static byte[] ToBytes(IReadOnlyList<float> values)
    {
        var bytes = new byte[values.Count * sizeof(float)];
        for (var index = 0; index < values.Count; index++)
            BitConverter.TryWriteBytes(bytes.AsSpan(index * sizeof(float), sizeof(float)), values[index]);
        return bytes;
    }

    private static float[] FromBytes(byte[] bytes)
    {
        if (bytes.Length % sizeof(float) != 0) return [];
        var values = new float[bytes.Length / sizeof(float)];
        for (var index = 0; index < values.Length; index++)
            values[index] = BitConverter.ToSingle(bytes, index * sizeof(float));
        return values;
    }

    private static string Format(DateTime value) => value.ToUniversalTime().ToString("o");
    private static DateTime Parse(string value) => DateTime.Parse(
        value, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
}
