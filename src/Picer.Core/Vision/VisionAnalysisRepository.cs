using System.Text.Json;
using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.Core.Vision;

public sealed class VisionAnalysisRepository(PicerDatabase database)
{
    public PicerDatabase Database => database;
    public void UpsertTrainingJob(string jobId, string endpoint, string status, string stage, int progress, string? artifactId = null, string? error = null)
    {
        using var command = database.Command("""
            INSERT INTO face_training_jobs(job_id,endpoint,status,stage,progress,artifact_id,error,updated_utc)
            VALUES($id,$endpoint,$status,$stage,$progress,$artifact,$error,$updated)
            ON CONFLICT(job_id) DO UPDATE SET endpoint=excluded.endpoint,status=excluded.status,stage=excluded.stage,
              progress=excluded.progress,artifact_id=excluded.artifact_id,error=excluded.error,updated_utc=excluded.updated_utc
            """);
        command.Parameters.AddWithValue("$id", jobId); command.Parameters.AddWithValue("$endpoint", endpoint);
        command.Parameters.AddWithValue("$status", status); command.Parameters.AddWithValue("$stage", stage);
        command.Parameters.AddWithValue("$progress", progress); command.Parameters.AddWithValue("$artifact", (object?)artifactId ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value); command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<(string JobId, string Endpoint, string Status, string Stage, int Progress, string? ArtifactId)> GetActiveTrainingJobs()
    {
        using var command = database.Command("SELECT job_id,endpoint,status,stage,progress,artifact_id FROM face_training_jobs WHERE status IN ('queued','running') ORDER BY updated_utc");
        using var reader = command.ExecuteReader(); var result = new List<(string,string,string,string,int,string?)>();
        while (reader.Read()) result.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        return result;
    }
    public IReadOnlyList<FaceTrainingArtifactSummary> GetFaceTrainingHistory(int limit = 20)
    {
        using var command = database.Command("SELECT artifact_id,dataset_id,model_version,false_accept_rate,false_reject_rate,state,created_utc,applied_utc FROM face_training_artifacts ORDER BY created_utc DESC LIMIT $limit");
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        using var reader = command.ExecuteReader(); var result = new List<FaceTrainingArtifactSummary>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3), reader.GetDouble(4), reader.GetString(5), DateTime.Parse(reader.GetString(6)), reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7))));
        return result;
    }
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
        var uniqueFaces = DeduplicateDetections(result.Faces);
        using var transaction = database.BeginTransaction();
        // Model versions are independent. Rebuilding InsightFace must not
        // remove the existing SFace (or another candidate model) records.
        DeleteFaces(image.Id, analyzer.ModelVersion, transaction);

        for (var ordinal = 0; ordinal < uniqueFaces.Count; ordinal++)
        {
            var face = uniqueFaces[ordinal];
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

        UpsertAnalysis(image, analyzer, "completed", uniqueFaces.Count, null, transaction);
        transaction.Commit();
    }

    private static IReadOnlyList<FaceDetection> DeduplicateDetections(IReadOnlyList<FaceDetection> faces)
    {
        var unique = new List<FaceDetection>(faces.Count);
        foreach (var face in faces.OrderByDescending(value => value.Confidence))
        {
            Validate(face);
            if (unique.Any(existing => IntersectionOverUnion(existing.Box, face.Box) >= 0.90 &&
                                       EmbeddingsMatch(existing.Embedding, face.Embedding))) continue;
            unique.Add(face);
        }
        return unique;
    }

    private static bool EmbeddingsMatch(IReadOnlyList<float>? left, IReadOnlyList<float>? right)
    {
        if (left is null || right is null) return true;
        var length = Math.Min(left.Count, right.Count);
        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var index = 0; index < length; index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }
        return length > 0 && leftNorm > 0 && rightNorm > 0 &&
               dot / Math.Sqrt(leftNorm * rightNorm) >= 0.995;
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
            WITH ranked_faces AS (
                SELECT f.*, ROW_NUMBER() OVER (
                    PARTITION BY COALESCE(NULLIF(i.sha256,''), printf('image:%d', i.id)), f.ordinal
                    ORDER BY f.id) AS duplicate_rank
                FROM detected_faces f JOIN images i ON i.id=f.image_id AND i.status='active'
            )
            SELECT f.id, f.image_id, i.path, i.thumb_path, f.ordinal,
                   f.box_x, f.box_y, f.box_width, f.box_height, f.confidence,
                   a.person_id,
                   p.display_name,
                   a.state,
                   i.sha256, i.ahash, i.width, i.height
            FROM ranked_faces f
            JOIN images i ON i.id=f.image_id AND i.status='active'
            LEFT JOIN face_person_assignments a ON a.face_id=f.id
            LEFT JOIN people p ON p.id=a.person_id
            LEFT JOIN face_review_states r ON r.face_id=f.id
            WHERE f.duplicate_rank=1 AND (a.face_id IS NULL OR a.state='suggested') AND r.face_id IS NULL
            ORDER BY f.created_utc DESC, f.id DESC
            LIMIT $limit
            """);
        var requestedLimit = Math.Clamp(limit, 1, 5000);
        command.Parameters.AddWithValue("$limit", Math.Min(20000, requestedLimit * 4));
        var values = new List<UnassignedFaceRecord>();
        var deduplicator = new FaceSourceDeduplicator();
        using var reader = command.ExecuteReader();
        while (reader.Read() && values.Count < requestedLimit)
        {
            if (deduplicator.IsDuplicateAndAdd(ReadSelectedFaceSignature(reader, 13))) continue;
            values.Add(new UnassignedFaceRecord(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4),
                new NormalizedFaceBox(reader.GetFloat(5), reader.GetFloat(6), reader.GetFloat(7), reader.GetFloat(8)),
                reader.GetFloat(9), reader.IsDBNull(10) ? null : reader.GetInt64(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12)));
        }
        return values;
    }

    public FaceWorkQueueCounts GetFaceWorkQueueCounts()
    {
        using var command = database.Command("""
            SELECT a.state, i.sha256, i.ahash, i.width, i.height,
                   f.box_x, f.box_y, f.box_width, f.box_height, f.confidence
            FROM detected_faces f
            JOIN images i ON i.id=f.image_id AND i.status='active'
            LEFT JOIN face_person_assignments a ON a.face_id=f.id
            LEFT JOIN face_review_states r ON r.face_id=f.id
            WHERE (a.face_id IS NULL OR a.state='suggested') AND r.face_id IS NULL
            ORDER BY CASE a.state WHEN 'suggested' THEN 0 ELSE 1 END, f.id
            """);
        var deduplicator = new FaceSourceDeduplicator();
        var suggested = 0;
        var unassigned = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var signature = new FaceSourceSignature(
                reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt64(2),
                reader.GetInt32(3), reader.GetInt32(4),
                new NormalizedFaceBox(reader.GetFloat(5), reader.GetFloat(6), reader.GetFloat(7), reader.GetFloat(8)));
            if (deduplicator.IsDuplicateAndAdd(signature)) continue;
            if (string.Equals(reader.IsDBNull(0) ? null : reader.GetString(0), "suggested", StringComparison.Ordinal)) suggested++;
            else unassigned++;
        }
        return new FaceWorkQueueCounts(suggested + unassigned, suggested, unassigned);
    }

    public IReadOnlyList<UnassignedFaceRecord> GetConfirmedFaces(long personId, int limit = 500)
    {
        using var command = database.Command("""
            WITH ranked_faces AS (
                SELECT f.*, ROW_NUMBER() OVER (
                    PARTITION BY COALESCE(NULLIF(i.sha256,''), printf('image:%d', i.id)), f.ordinal
                    ORDER BY f.id) AS duplicate_rank
                FROM detected_faces f JOIN images i ON i.id=f.image_id AND i.status='active'
            )
            SELECT f.id, f.image_id, i.path, i.thumb_path, f.ordinal,
                   f.box_x, f.box_y, f.box_width, f.box_height, f.confidence,
                   i.sha256, i.ahash, i.width, i.height
            FROM face_person_assignments a
            JOIN ranked_faces f ON f.id=a.face_id AND f.duplicate_rank=1
            JOIN images i ON i.id=f.image_id AND i.status='active'
            WHERE a.person_id=$person AND a.state='confirmed'
            ORDER BY f.created_utc DESC, f.id DESC
            LIMIT $limit
            """);
        command.Parameters.AddWithValue("$person", personId);
        var requestedLimit = Math.Clamp(limit, 1, 5000);
        command.Parameters.AddWithValue("$limit", Math.Min(20000, requestedLimit * 4));
        var values = new List<UnassignedFaceRecord>();
        var deduplicator = new FaceSourceDeduplicator(collapseSameSourceIdentity: true);
        using var reader = command.ExecuteReader();
        while (reader.Read() && values.Count < requestedLimit)
        {
            if (deduplicator.IsDuplicateAndAdd(ReadSelectedFaceSignature(reader, 10), reader.GetInt64(1), reader.GetString(2))) continue;
            values.Add(new UnassignedFaceRecord(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4),
                new NormalizedFaceBox(reader.GetFloat(5), reader.GetFloat(6), reader.GetFloat(7), reader.GetFloat(8)),
                reader.GetFloat(9)));
        }
        return values;
    }

    public IReadOnlyList<SuggestedPersonGroupRecord> GetSuggestedPersonGroups()
    {
        using var command = database.Command("""
            SELECT p.id, p.display_name, f.id, f.image_id, i.path, i.thumb_path, f.ordinal,
                   f.box_x, f.box_y, f.box_width, f.box_height, f.confidence,
                   i.sha256, i.ahash, i.width, i.height, f.embedding
            FROM face_person_assignments a
            JOIN people p ON p.id=a.person_id
            JOIN detected_faces f ON f.id=a.face_id
            JOIN images i ON i.id=f.image_id AND i.status='active'
            LEFT JOIN face_review_states r ON r.face_id=f.id
            WHERE a.state='suggested' AND r.face_id IS NULL
            ORDER BY p.display_name, f.created_utc DESC, f.id DESC
            """);
        var groups = new Dictionary<long, (string Name, List<UnassignedFaceRecord> Faces, FaceSourceDeduplicator Deduplicator)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var personId = reader.GetInt64(0);
            if (!groups.TryGetValue(personId, out var group))
            {
                group = (reader.GetString(1), [], new FaceSourceDeduplicator(collapseSameSourceIdentity: true));
                groups.Add(personId, group);
            }
            var signature = new FaceSourceSignature(
                reader.IsDBNull(12) ? null : reader.GetString(12), reader.GetInt64(13),
                reader.GetInt32(14), reader.GetInt32(15),
                new NormalizedFaceBox(reader.GetFloat(7), reader.GetFloat(8), reader.GetFloat(9), reader.GetFloat(10)),
                reader.IsDBNull(16) ? null : FromBytes((byte[])reader[16]));
            if (group.Deduplicator.IsDuplicateAndAdd(signature, reader.GetInt64(3), reader.GetString(4))) continue;
            group.Faces.Add(new UnassignedFaceRecord(
                reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt32(6),
                signature.Box, reader.GetFloat(11), personId, group.Name));
        }
        return groups.Select(pair => new SuggestedPersonGroupRecord(pair.Key, pair.Value.Name, pair.Value.Faces))
            .Where(group => group.Faces.Count > 0).OrderByDescending(group => group.Faces.Count).ToArray();
    }

    public IReadOnlyList<UnassignedFaceRecord> GetPersonFaces(long personId, int limit = 500)
    {
        using var command = database.Command("""
            WITH ranked_faces AS (
                SELECT f.*, ROW_NUMBER() OVER (
                    PARTITION BY COALESCE(NULLIF(i.sha256,''), printf('image:%d', i.id)), f.ordinal
                    ORDER BY f.id) AS duplicate_rank
                FROM detected_faces f JOIN images i ON i.id=f.image_id AND i.status='active'
            )
            SELECT f.id, f.image_id, i.path, i.thumb_path, f.ordinal,
                   f.box_x, f.box_y, f.box_width, f.box_height, f.confidence,
                   a.person_id,
                   p.display_name,
                   a.state,
                   i.sha256, i.ahash, i.width, i.height
            FROM face_person_assignments a
            JOIN ranked_faces f ON f.id=a.face_id AND f.duplicate_rank=1
            JOIN images i ON i.id=f.image_id AND i.status='active'
            JOIN people p ON p.id=a.person_id
            WHERE a.person_id=$person AND a.state IN ('confirmed','suggested')
            ORDER BY CASE a.state WHEN 'suggested' THEN 0 ELSE 1 END, f.created_utc DESC, f.id DESC
            LIMIT $limit
            """);
        command.Parameters.AddWithValue("$person", personId);
        var requestedLimit = Math.Clamp(limit, 1, 5000);
        command.Parameters.AddWithValue("$limit", Math.Min(20000, requestedLimit * 4));
        var values = new List<UnassignedFaceRecord>();
        var deduplicator = new FaceSourceDeduplicator(collapseSameSourceIdentity: true);
        using var reader = command.ExecuteReader();
        while (reader.Read() && values.Count < requestedLimit)
        {
            if (deduplicator.IsDuplicateAndAdd(ReadSelectedFaceSignature(reader, 13), reader.GetInt64(1), reader.GetString(2))) continue;
            values.Add(new UnassignedFaceRecord(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4),
                new NormalizedFaceBox(reader.GetFloat(5), reader.GetFloat(6), reader.GetFloat(7), reader.GetFloat(8)),
                reader.GetFloat(9), reader.IsDBNull(10) ? null : reader.GetInt64(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12)));
        }
        return values;
    }

    public IReadOnlyList<UnassignedFaceRecord> GetIgnoredFaces(int limit = 500)
    {
        using var command = database.Command("""
            WITH ranked_faces AS (
                SELECT f.*, ROW_NUMBER() OVER (
                    PARTITION BY COALESCE(NULLIF(i.sha256,''), printf('image:%d', i.id)), f.ordinal
                    ORDER BY f.id) AS duplicate_rank
                FROM detected_faces f JOIN images i ON i.id=f.image_id AND i.status='active'
            )
            SELECT f.id, f.image_id, i.path, i.thumb_path, f.ordinal,
                   f.box_x, f.box_y, f.box_width, f.box_height, f.confidence,
                   i.sha256, i.ahash, i.width, i.height
            FROM face_review_states r
            JOIN ranked_faces f ON f.id=r.face_id AND f.duplicate_rank=1
            JOIN images i ON i.id=f.image_id AND i.status='active'
            WHERE r.state='ignored'
            ORDER BY r.updated_utc DESC, f.id DESC
            LIMIT $limit
            """);
        var requestedLimit = Math.Clamp(limit, 1, 5000);
        command.Parameters.AddWithValue("$limit", Math.Min(20000, requestedLimit * 4));
        var values = new List<UnassignedFaceRecord>();
        var deduplicator = new FaceSourceDeduplicator();
        using var reader = command.ExecuteReader();
        while (reader.Read() && values.Count < requestedLimit)
        {
            if (deduplicator.IsDuplicateAndAdd(ReadSelectedFaceSignature(reader, 10))) continue;
            values.Add(new UnassignedFaceRecord(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4),
                new NormalizedFaceBox(reader.GetFloat(5), reader.GetFloat(6), reader.GetFloat(7), reader.GetFloat(8)),
                reader.GetFloat(9)));
        }
        return values;
    }

    public IReadOnlyList<FaceEmbeddingRecord> GetEmbeddingsForMatching()
    {
        using var command = database.Command("""
            SELECT f.id, f.image_id, f.model_version, a.person_id, a.state, f.embedding,
                   i.sha256, i.ahash, i.width, i.height,
                   f.box_x, f.box_y, f.box_width, f.box_height, f.confidence
            FROM detected_faces f
            JOIN images i ON i.id=f.image_id AND i.status='active'
            LEFT JOIN face_person_assignments a ON a.face_id=f.id
            LEFT JOIN face_review_states r ON r.face_id=f.id
            WHERE f.embedding IS NOT NULL AND r.face_id IS NULL
              AND (a.face_id IS NULL OR a.state IN ('suggested','confirmed'))
            ORDER BY f.id
            """);
        var values = new List<FaceEmbeddingRecord>();
        var deduplicator = new FaceSourceDeduplicator();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var signature = ReadFaceSourceSignature(reader, 6);
            if (deduplicator.IsDuplicateAndAdd(signature)) continue;
            values.Add(new FaceEmbeddingRecord(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), FromBytes((byte[])reader[5]),
                reader.GetFloat(14), reader.GetFloat(12) * reader.GetFloat(13)));
        }
        return values;
    }

    public IReadOnlyDictionary<long, float[]> GetFaceEmbeddings(IEnumerable<long> faceIds)
    {
        var ids = faceIds.Distinct().ToArray();
        var result = new Dictionary<long, float[]>();
        foreach (var batch in ids.Chunk(400))
        {
            var names = batch.Select((_, index) => $"$face{index}").ToArray();
            using var command = database.Command($"SELECT id,embedding FROM detected_faces WHERE embedding IS NOT NULL AND id IN ({string.Join(',', names)})");
            for (var index = 0; index < batch.Length; index++) command.Parameters.AddWithValue(names[index], batch[index]);
            using var reader = command.ExecuteReader();
            while (reader.Read()) result[reader.GetInt64(0)] = FromBytes((byte[])reader[1]);
        }
        return result;
    }

    public IReadOnlyList<UnassignedFaceRecord> OrderByFeature(IReadOnlyList<UnassignedFaceRecord> faces) =>
        FaceFeatureOrderer.Order(faces, GetFaceEmbeddings(faces.Select(face => face.FaceId)));

    public IReadOnlyList<RejectedFaceEmbeddingRecord> GetRejectedFaceEmbeddings()
    {
        using var command = database.Command("""
            SELECT f.id, f.image_id, f.model_version, r.person_id, f.embedding,f.confidence,f.box_width*f.box_height
            FROM face_person_rejections r
            JOIN detected_faces f ON f.id=r.face_id
            JOIN images i ON i.id=f.image_id AND i.status='active'
            WHERE f.embedding IS NOT NULL
            ORDER BY r.person_id, r.face_id
            """);
        var values = new List<RejectedFaceEmbeddingRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) values.Add(new RejectedFaceEmbeddingRecord(reader.GetInt64(0), reader.GetInt64(1),
            reader.GetString(2), reader.GetInt64(3), FromBytes((byte[])reader[4]), reader.GetFloat(5), reader.GetFloat(6)));
        return values;
    }

    public IReadOnlyDictionary<long, string> GetTrainingLeakageGroups(IEnumerable<long> imageIds)
    {
        var ids = imageIds.Distinct().ToArray();
        var result = new Dictionary<long, string>();
        foreach (var batch in ids.Chunk(400))
        {
            var names = batch.Select((_, index) => $"$id{index}").ToArray();
            using var command = database.Command($"""
                SELECT i.id,i.sha256,bgm.group_id
                FROM images i
                LEFT JOIN burst_group_members bgm ON bgm.image_id=i.id
                WHERE i.id IN ({string.Join(',', names)})
                """);
            for (var index = 0; index < batch.Length; index++) command.Parameters.AddWithValue(names[index], batch[index]);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var imageId = reader.GetInt64(0);
                result[imageId] = !reader.IsDBNull(2) ? $"burst:{reader.GetInt64(2)}"
                    : !reader.IsDBNull(1) && !string.IsNullOrWhiteSpace(reader.GetString(1)) ? $"sha256:{reader.GetString(1)}"
                    : $"image:{imageId}";
            }
        }
        return result;
    }

    public IReadOnlyList<VideoTrainingFaceRecord> GetVideoTrainingFaces()
    {
        using var command = database.Command("""
            SELECT vf.id,vf.video_id,vf.assigned_person_id,'positive',vf.embedding,vf.confidence,vf.box_width*vf.box_height
            FROM video_detected_faces vf JOIN videos v ON v.id=vf.video_id AND v.status='active'
            WHERE vf.assignment_state='confirmed' AND vf.assigned_person_id IS NOT NULL AND vf.embedding IS NOT NULL
            UNION ALL
            SELECT vf.id,vf.video_id,r.person_id,'negative',vf.embedding,vf.confidence,vf.box_width*vf.box_height
            FROM video_face_person_rejections r
            JOIN video_detected_faces vf ON vf.id=r.video_face_id
            JOIN videos v ON v.id=vf.video_id AND v.status='active'
            WHERE vf.embedding IS NOT NULL
            ORDER BY 2,1,3
            """);
        var result = new List<VideoTrainingFaceRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.GetString(3), FromBytes((byte[])reader[4]), reader.GetFloat(5), reader.GetFloat(6)));
        return result;
    }

    public double? GetFaceMatchThreshold(string modelVersion)
    {
        using var command = database.Command("SELECT match_threshold FROM face_model_settings WHERE model_version=$version");
        command.Parameters.AddWithValue("$version", modelVersion);
        return command.ExecuteScalar() is { } value ? Convert.ToDouble(value) : null;
    }

    public IReadOnlyDictionary<long, FaceLearnedPolicy> GetLearnedFacePolicies(string modelVersion)
    {
        using var command = database.Command("""
            SELECT person_id,artifact_id,model_version,prototype,match_threshold
            FROM face_person_policies WHERE model_version=$model
            """);
        command.Parameters.AddWithValue("$model", modelVersion);
        var values = new Dictionary<long, FaceLearnedPolicy>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var value = new FaceLearnedPolicy(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                FromBytes((byte[])reader[3]), reader.GetDouble(4));
            values[value.PersonId] = value;
        }
        return values;
    }

    public float[]? GetActiveFaceAdapter(string modelVersion)
    {
        using var command = database.Command("""
            SELECT artifact_json FROM face_training_artifacts
            WHERE state='active' AND model_version=$model ORDER BY applied_utc DESC LIMIT 1
            """);
        command.Parameters.AddWithValue("$model", modelVersion);
        if (command.ExecuteScalar() is not string json) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("adapter", out var adapter) ||
                !adapter.TryGetProperty("weights", out var weights) || weights.ValueKind != JsonValueKind.Array) return null;
            var values = weights.EnumerateArray().Select(value => value.GetSingle()).ToArray();
            return values.Length > 0 && values.All(float.IsFinite) ? values : null;
        }
        catch (JsonException) { return null; }
    }

    public string GetActiveFaceAdapterVersion(string modelVersion)
    {
        using var command = database.Command("SELECT artifact_id FROM face_training_artifacts WHERE state='active' AND model_version=$model ORDER BY applied_utc DESC LIMIT 1");
        command.Parameters.AddWithValue("$model", modelVersion);
        return command.ExecuteScalar() as string ?? "none";
    }

    public void ApplyLearnedFaceArtifact(string artifactId, string datasetId, string endpoint, string modelVersion,
        string artifactJson, double falseAcceptRate, double falseRejectRate,
        IReadOnlyCollection<FaceLearnedPolicy> policies)
    {
        using var transaction = database.BeginTransaction();
        using (var supersede = database.Command("UPDATE face_training_artifacts SET state='superseded' WHERE state='active'"))
        { supersede.Transaction = transaction; supersede.ExecuteNonQuery(); }
        using (var artifact = database.Command("""
            INSERT INTO face_training_artifacts(artifact_id,dataset_id,endpoint,model_version,artifact_json,
                false_accept_rate,false_reject_rate,state,created_utc,applied_utc)
            VALUES($id,$dataset,$endpoint,$model,$json,$far,$frr,'active',$now,$now)
            ON CONFLICT(artifact_id) DO UPDATE SET state='active',applied_utc=excluded.applied_utc
            """))
        {
            artifact.Transaction = transaction;
            artifact.Parameters.AddWithValue("$id", artifactId); artifact.Parameters.AddWithValue("$dataset", datasetId);
            artifact.Parameters.AddWithValue("$endpoint", endpoint); artifact.Parameters.AddWithValue("$model", modelVersion);
            artifact.Parameters.AddWithValue("$json", artifactJson); artifact.Parameters.AddWithValue("$far", falseAcceptRate);
            artifact.Parameters.AddWithValue("$frr", falseRejectRate); artifact.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            artifact.ExecuteNonQuery();
        }
        using (var clear = database.Command("DELETE FROM face_person_policies")) { clear.Transaction = transaction; clear.ExecuteNonQuery(); }
        foreach (var policy in policies)
        {
            using var insert = database.Command("""
                INSERT INTO face_person_policies(person_id,artifact_id,model_version,prototype,match_threshold,updated_utc)
                VALUES($person,$artifact,$model,$prototype,$threshold,$now)
                """);
            insert.Transaction = transaction;
            insert.Parameters.AddWithValue("$person", policy.PersonId); insert.Parameters.AddWithValue("$artifact", artifactId);
            insert.Parameters.AddWithValue("$model", modelVersion); insert.Parameters.AddWithValue("$prototype", ToBytes(policy.Prototype));
            insert.Parameters.AddWithValue("$threshold", policy.MatchThreshold); insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public (string ArtifactId, string DatasetId, string Endpoint, string ModelVersion, string Json,
        double FalseAcceptRate, double FalseRejectRate)? GetPreviousLearnedFaceArtifact()
    {
        using var command = database.Command("""
            SELECT artifact_id,dataset_id,endpoint,model_version,artifact_json,false_accept_rate,false_reject_rate
            FROM face_training_artifacts WHERE state='superseded' ORDER BY applied_utc DESC,created_utc DESC LIMIT 1
            """);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetDouble(5), reader.GetDouble(6)) : null;
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
                   f.box_x, f.box_y, f.box_width, f.box_height, f.confidence,
                   i.sha256, i.ahash, i.width, i.height
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
        var deduplicators = new Dictionary<long, FaceSourceDeduplicator>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var clusterId = reader.GetInt64(0);
            if (!groups.TryGetValue(clusterId, out var group))
            {
                group = (reader.GetString(1), []);
                groups.Add(clusterId, group);
                deduplicators.Add(clusterId, new FaceSourceDeduplicator(collapseSameSourceIdentity: true));
            }
            var signature = new FaceSourceSignature(
                reader.IsDBNull(12) ? null : reader.GetString(12), reader.GetInt64(13),
                reader.GetInt32(14), reader.GetInt32(15),
                new NormalizedFaceBox(reader.GetFloat(7), reader.GetFloat(8), reader.GetFloat(9), reader.GetFloat(10)));
            if (deduplicators[clusterId].IsDuplicateAndAdd(signature, reader.GetInt64(3), reader.GetString(4))) continue;
            group.Faces.Add(new UnassignedFaceRecord(
                reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt32(6),
                new NormalizedFaceBox(reader.GetFloat(7), reader.GetFloat(8), reader.GetFloat(9), reader.GetFloat(10)),
                reader.GetFloat(11)));
        }
        return groups.Where(pair => pair.Value.Faces.Count >= 2)
            .Select(pair => new FaceCandidateClusterRecord(pair.Key, pair.Value.Version, pair.Value.Faces)).ToArray();
    }

    private static FaceSourceSignature ReadFaceSourceSignature(SqliteDataReader reader, int offset) => new(
        reader.IsDBNull(offset) ? null : reader.GetString(offset), reader.GetInt64(offset + 1),
        reader.GetInt32(offset + 2), reader.GetInt32(offset + 3),
        new NormalizedFaceBox(reader.GetFloat(offset + 4), reader.GetFloat(offset + 5),
            reader.GetFloat(offset + 6), reader.GetFloat(offset + 7)));

    private static FaceSourceSignature ReadSelectedFaceSignature(SqliteDataReader reader, int signatureOffset) => new(
        reader.IsDBNull(signatureOffset) ? null : reader.GetString(signatureOffset),
        reader.GetInt64(signatureOffset + 1), reader.GetInt32(signatureOffset + 2),
        reader.GetInt32(signatureOffset + 3),
        new NormalizedFaceBox(reader.GetFloat(5), reader.GetFloat(6), reader.GetFloat(7), reader.GetFloat(8)));

    private static bool IsDuplicateFaceSource(FaceSourceSignature left, FaceSourceSignature right)
    {
        var sameImage = !string.IsNullOrWhiteSpace(left.Sha256) &&
                        string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase);
        var sameVisual = left.AverageHash != 0 && left.AverageHash == right.AverageHash &&
                         left.Width == right.Width && left.Height == right.Height;
        return (sameImage || sameVisual) && IntersectionOverUnion(left.Box, right.Box) >= 0.90;
    }

    private static double IntersectionOverUnion(NormalizedFaceBox left, NormalizedFaceBox right)
    {
        var intersectionWidth = Math.Max(0, Math.Min(left.X + left.Width, right.X + right.Width) - Math.Max(left.X, right.X));
        var intersectionHeight = Math.Max(0, Math.Min(left.Y + left.Height, right.Y + right.Height) - Math.Max(left.Y, right.Y));
        var intersection = intersectionWidth * intersectionHeight;
        var union = left.Width * left.Height + right.Width * right.Height - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private sealed record FaceSourceSignature(string? Sha256, long AverageHash, int Width, int Height,
        NormalizedFaceBox Box, float[]? Embedding = null);

    private sealed class FaceSourceDeduplicator
    {
        private readonly bool _collapseSameSourceIdentity;
        private readonly HashSet<long> _imageIds = [];
        private readonly HashSet<string> _sourceHashes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _sourceNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<FaceSourceSignature>> _bySha256 = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(long Hash, int Width, int Height), List<FaceSourceSignature>> _byVisual = [];
        private readonly List<FaceSourceSignature> _withEmbedding = [];

        public FaceSourceDeduplicator(bool collapseSameSourceIdentity = false) =>
            _collapseSameSourceIdentity = collapseSameSourceIdentity;

        public bool IsDuplicateAndAdd(FaceSourceSignature signature, long? imageId = null, string? path = null)
        {
            var sourceName = string.IsNullOrWhiteSpace(path) ? null : Path.GetFileName(path);
            if (_collapseSameSourceIdentity &&
                ((imageId is long id && _imageIds.Contains(id)) ||
                 (!string.IsNullOrWhiteSpace(signature.Sha256) && _sourceHashes.Contains(signature.Sha256)) ||
                 (!string.IsNullOrWhiteSpace(sourceName) && _sourceNames.Contains(sourceName)))) return true;

            if (!string.IsNullOrWhiteSpace(signature.Sha256) &&
                ContainsDuplicate(_bySha256.GetValueOrDefault(signature.Sha256), signature)) return true;
            var visualKey = (signature.AverageHash, signature.Width, signature.Height);
            if (signature.AverageHash != 0 &&
                ContainsDuplicate(_byVisual.GetValueOrDefault(visualKey), signature)) return true;
            if (signature.AverageHash != 0 && signature.Embedding is { Length: > 0 } &&
                _withEmbedding.Any(existing => IsNearDuplicateFaceSource(existing, signature))) return true;

            if (!string.IsNullOrWhiteSpace(signature.Sha256))
                Add(_bySha256, signature.Sha256, signature);
            if (signature.AverageHash != 0)
                Add(_byVisual, visualKey, signature);
            if (signature.Embedding is { Length: > 0 }) _withEmbedding.Add(signature);
            if (_collapseSameSourceIdentity)
            {
                if (imageId is long sourceImageId) _imageIds.Add(sourceImageId);
                if (!string.IsNullOrWhiteSpace(signature.Sha256)) _sourceHashes.Add(signature.Sha256);
                if (!string.IsNullOrWhiteSpace(sourceName)) _sourceNames.Add(sourceName);
            }
            return false;
        }

        private static bool ContainsDuplicate(List<FaceSourceSignature>? values, FaceSourceSignature candidate) =>
            values is not null && values.Any(existing => IsDuplicateFaceSource(existing, candidate));

        private static void Add<TKey>(Dictionary<TKey, List<FaceSourceSignature>> index, TKey key,
            FaceSourceSignature value) where TKey : notnull
        {
            if (!index.TryGetValue(key, out var values)) index.Add(key, values = []);
            values.Add(value);
        }
    }

    private static bool IsNearDuplicateFaceSource(FaceSourceSignature left, FaceSourceSignature right)
    {
        if (left.AverageHash == 0 || right.AverageHash == 0 ||
            left.Embedding is not { Length: > 0 } leftEmbedding ||
            right.Embedding is not { Length: > 0 } rightEmbedding) return false;
        if (HammingDistance(left.AverageHash, right.AverageHash) > 1) return false;
        var leftAspect = left.Height == 0 ? 0 : (double)left.Width / left.Height;
        var rightAspect = right.Height == 0 ? 0 : (double)right.Width / right.Height;
        if (leftAspect <= 0 || rightAspect <= 0 || Math.Abs(leftAspect - rightAspect) / leftAspect > 0.01) return false;
        return IntersectionOverUnion(left.Box, right.Box) >= 0.75 &&
               CosineSimilarity(leftEmbedding, rightEmbedding) >= 0.99;
    }

    private static int HammingDistance(long left, long right) =>
        System.Numerics.BitOperations.PopCount(unchecked((ulong)(left ^ right)));

    private static double CosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        var length = Math.Min(left.Count, right.Count);
        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var index = 0; index < length; index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }
        return length == 0 || leftNorm <= 0 || rightNorm <= 0 ? -1 : dot / Math.Sqrt(leftNorm * rightNorm);
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

    public IReadOnlyList<FaceVectorBackfillSource> GetFaceSources(string modelVersion)
    {
        using var command = database.Command("""
            SELECT f.id,f.image_id,i.path,f.ordinal,f.box_x,f.box_y,f.box_width,f.box_height,f.confidence
            FROM detected_faces f JOIN images i ON i.id=f.image_id
            WHERE f.model_version=$model AND i.status='active'
            ORDER BY f.image_id,f.ordinal
            """);
        command.Parameters.AddWithValue("$model", modelVersion);
        var result = new List<FaceVectorBackfillSource>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(new FaceVectorBackfillSource(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
            reader.GetInt32(3), new NormalizedFaceBox(reader.GetFloat(4), reader.GetFloat(5), reader.GetFloat(6), reader.GetFloat(7)), reader.GetFloat(8)));
        return result;
    }

    public IReadOnlyList<string> GetFaceModelVersions()
    {
        using var command = database.Command("SELECT DISTINCT model_version FROM detected_faces ORDER BY model_version");
        using var reader = command.ExecuteReader();
        var versions = new List<string>();
        while (reader.Read()) versions.Add(reader.GetString(0));
        return versions;
    }

    public int GetFaceEmbeddingCount(string modelVersion)
    {
        using var command = database.Command("SELECT COUNT(*) FROM detected_faces WHERE model_version=$model AND embedding IS NOT NULL");
        command.Parameters.AddWithValue("$model", modelVersion);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>Reads vectors for an immutable evaluation snapshot from one model version.
    /// The snapshot member's source face is joined by image and ordinal, so model-specific
    /// vectors can never be mixed or silently reused across versions.</summary>
    public IReadOnlyList<FaceSnapshotModelSample> GetSnapshotModelSamples(string snapshotId, string modelVersion)
    {
        using var command = database.Command("""
            SELECT m.face_id,m.person_id,m.partition,m.leakage_group,target.embedding
            FROM face_dataset_members m
            JOIN detected_faces source ON source.id=m.face_id
            JOIN detected_faces target ON target.image_id=source.image_id
                AND target.ordinal=source.ordinal AND target.model_version=$model
            WHERE m.snapshot_id=$snapshot AND target.embedding IS NOT NULL
            ORDER BY m.sample_key
            """);
        command.Parameters.AddWithValue("$snapshot", snapshotId);
        command.Parameters.AddWithValue("$model", modelVersion);
        using var reader = command.ExecuteReader();
        var result = new List<FaceSnapshotModelSample>();
        while (reader.Read())
        {
            var bytes = (byte[])reader[4];
            var embedding = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, embedding, 0, bytes.Length);
            result.Add(new FaceSnapshotModelSample(reader.GetInt64(0), reader.GetInt64(1),
                reader.GetString(2), reader.GetString(3), embedding));
        }
        return result;
    }

    public int SaveBackfilledEmbeddings(string sourceModelVersion, string targetModelVersion,
        IReadOnlyList<(FaceVectorBackfillSource Source, float[] Embedding)> values)
    {
        if (values.Count == 0) return 0;
        using var transaction = database.BeginTransaction();
        var saved = 0;
        foreach (var value in values)
        {
            using var insert = database.Command("""
                INSERT INTO detected_faces(image_id,model_version,ordinal,box_x,box_y,box_width,box_height,confidence,created_utc)
                VALUES($image,$model,$ordinal,$x,$y,$w,$h,$confidence,$created)
                ON CONFLICT(image_id,model_version,ordinal) DO UPDATE SET embedding=NULL,box_x=excluded.box_x,box_y=excluded.box_y,
                    box_width=excluded.box_width,box_height=excluded.box_height,confidence=excluded.confidence
                """);
            insert.Transaction = transaction;
            insert.Parameters.AddWithValue("$image", value.Source.ImageId);
            insert.Parameters.AddWithValue("$model", targetModelVersion);
            insert.Parameters.AddWithValue("$ordinal", value.Source.Ordinal);
            insert.Parameters.AddWithValue("$x", value.Source.Box.X); insert.Parameters.AddWithValue("$y", value.Source.Box.Y);
            insert.Parameters.AddWithValue("$w", value.Source.Box.Width); insert.Parameters.AddWithValue("$h", value.Source.Box.Height);
            insert.Parameters.AddWithValue("$confidence", value.Source.Confidence); insert.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
            using var update = database.Command("UPDATE detected_faces SET embedding=$embedding WHERE image_id=$image AND model_version=$model AND ordinal=$ordinal");
            update.Transaction = transaction; update.Parameters.Add("$embedding", Microsoft.Data.Sqlite.SqliteType.Blob).Value = ToBytes(value.Embedding);
            update.Parameters.AddWithValue("$image", value.Source.ImageId); update.Parameters.AddWithValue("$model", targetModelVersion); update.Parameters.AddWithValue("$ordinal", value.Source.Ordinal); update.ExecuteNonQuery();
            using var copy = database.Command("""
                INSERT OR IGNORE INTO face_person_assignments(face_id,person_id,state,confidence,assigned_by,updated_utc)
                SELECT target.id,a.person_id,a.state,a.confidence,a.assigned_by,a.updated_utc
                FROM detected_faces source JOIN face_person_assignments a ON a.face_id=source.id
                JOIN detected_faces target ON target.image_id=source.image_id AND target.ordinal=source.ordinal AND target.model_version=$target
                WHERE source.id=$source AND source.model_version=$sourceModel
                """);
            copy.Transaction = transaction; copy.Parameters.AddWithValue("$target", targetModelVersion); copy.Parameters.AddWithValue("$source", value.Source.FaceId); copy.Parameters.AddWithValue("$sourceModel", sourceModelVersion); copy.ExecuteNonQuery();
            saved++;
        }
        transaction.Commit();
        return saved;
    }

    private void DeleteFaces(long imageId, string modelVersion, SqliteTransaction transaction)
    {
        using var command = database.Command("DELETE FROM detected_faces WHERE image_id = $image_id AND model_version = $model_version");
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$image_id", imageId);
        command.Parameters.AddWithValue("$model_version", modelVersion);
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
