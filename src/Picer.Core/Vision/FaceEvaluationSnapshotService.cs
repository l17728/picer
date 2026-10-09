using System.Security.Cryptography;
using System.Text;
using Picer.Core.Data;

namespace Picer.Core.Vision;

public sealed record FaceEvaluationSnapshot(string SnapshotId, DateTime CreatedUtc, int RandomSeed,
    string DataVersion, string AlgorithmId, string EmbeddingModelVersion, string AdapterVersion, int Samples,
    int TrainSamples, int ValidationSamples, int TestSamples)
{
    [Obsolete("Use EmbeddingModelVersion; retained for compatibility with older reports.")]
    public string SFaceVersion => EmbeddingModelVersion;
}

public sealed record FaceEvaluationSnapshotMember(string SnapshotId, string SampleKey, long? FaceId,
    long? VideoFaceId, long PersonId, string Label, string Partition, string LeakageGroup,
    string? SourceHash, string EmbeddingHash, double QualityScore, float[] Embedding);

/// <summary>Creates immutable, reproducible face evaluation snapshots with leakage-safe partitions.</summary>
public sealed class FaceEvaluationSnapshotService(VisionAnalysisRepository repository)
{
    public FaceEvaluationSnapshot CreateSnapshot(int randomSeed = 20260907,
        string dataVersion = "face-labels-v1", string? adapterVersion = null,
        string algorithmId = "face-embedding", string? embeddingModelVersion = null)
    {
        var all = repository.GetEmbeddingsForMatching();
        var positives = all.Where(face => face.AssignmentState == "confirmed" && face.PersonId is not null).ToArray();
        if (positives.Length == 0) throw new InvalidOperationException("没有可用于评测快照的已确认人脸。");
        var modelVersion = embeddingModelVersion ?? positives.GroupBy(face => face.ModelVersion).OrderByDescending(group => group.Count())
            .Select(group => group.Key).First();
        var selected = positives.Where(face => face.ModelVersion == modelVersion).ToArray();
        var rejected = repository.GetRejectedFaceEmbeddings().Where(face => face.ModelVersion == modelVersion).ToArray();
        var imageGroups = repository.GetTrainingLeakageGroups(selected.Select(face => face.ImageId)
            .Concat(rejected.Select(face => face.ImageId)));
        var members = new List<FaceEvaluationSnapshotMember>();
        foreach (var face in selected)
        {
            var group = imageGroups.GetValueOrDefault(face.ImageId, $"image:{face.ImageId}");
            members.Add(CreateMember($"face:{face.FaceId}", face.FaceId, null, face.PersonId!.Value, "positive", group,
                group, face.Embedding, Quality(face.DetectionConfidence, face.RelativeArea), randomSeed));
        }
        foreach (var face in rejected)
        {
            var group = imageGroups.GetValueOrDefault(face.ImageId, $"image:{face.ImageId}");
            members.Add(CreateMember($"face:{face.FaceId}:negative:{face.PersonId}", face.FaceId, null, face.PersonId,
                "negative", group, group, face.Embedding, Quality(face.DetectionConfidence, face.RelativeArea), randomSeed));
        }
        foreach (var face in repository.GetVideoTrainingFaces(modelVersion))
        {
            var group = $"video:{face.VideoId}"; // all keyframes from a video share one partition
            members.Add(CreateMember($"video:{face.VideoFaceId}:{face.PersonId}:{face.Label}", null, face.VideoFaceId,
                face.PersonId, face.Label, group, group, face.Embedding, Quality(face.DetectionConfidence, face.RelativeArea), randomSeed));
        }
        members = members.OrderBy(member => member.SampleKey, StringComparer.Ordinal).ToList();
        var adapter = adapterVersion ?? "none";
        var canonical = string.Join("\n", members.Select(member =>
            $"{member.SampleKey}|{member.PersonId}|{member.Label}|{member.Partition}|{member.LeakageGroup}|{member.EmbeddingHash}"));
        var snapshotId = "face-snapshot-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{dataVersion}|{algorithmId}|{modelVersion}|{adapter}|{randomSeed}|{canonical}")))[..24].ToLowerInvariant();
        var train = members.Count(member => member.Partition == "train");
        var validation = members.Count(member => member.Partition == "validation");
        var test = members.Count(member => member.Partition == "test");
        using var tx = repository.Database.BeginTransaction();
        using (var insert = repository.Database.Command("""
            INSERT OR IGNORE INTO face_dataset_snapshots(snapshot_id,created_utc,random_seed,data_version,sface_version,adapter_version,sample_count,train_count,validation_count,test_count,algorithm_id,embedding_model_version)
            VALUES($id,$created,$seed,$data,$sface,$adapter,$samples,$train,$validation,$test,$algorithm,$embedding)
            """))
        {
            insert.Transaction = tx; insert.Parameters.AddWithValue("$id", snapshotId);
            insert.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("o")); insert.Parameters.AddWithValue("$seed", randomSeed);
            insert.Parameters.AddWithValue("$data", dataVersion); insert.Parameters.AddWithValue("$sface", modelVersion);
            insert.Parameters.AddWithValue("$algorithm", algorithmId); insert.Parameters.AddWithValue("$embedding", modelVersion);
            insert.Parameters.AddWithValue("$adapter", adapter); insert.Parameters.AddWithValue("$samples", members.Count);
            insert.Parameters.AddWithValue("$train", train); insert.Parameters.AddWithValue("$validation", validation); insert.Parameters.AddWithValue("$test", test);
            insert.ExecuteNonQuery();
        }
        foreach (var member in members)
        {
            using var insert = repository.Database.Command("""
                INSERT OR IGNORE INTO face_dataset_members(snapshot_id,sample_key,face_id,video_face_id,person_id,label,partition,leakage_group,source_hash,embedding_hash,quality_score,embedding)
                VALUES($snapshot,$key,$face,$video,$person,$label,$partition,$group,$source,$embeddingHash,$quality,$embedding)
                """);
            insert.Transaction = tx;
            insert.Parameters.AddWithValue("$snapshot", snapshotId); insert.Parameters.AddWithValue("$key", member.SampleKey);
            insert.Parameters.AddWithValue("$face", (object?)member.FaceId ?? DBNull.Value); insert.Parameters.AddWithValue("$video", (object?)member.VideoFaceId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$person", member.PersonId); insert.Parameters.AddWithValue("$label", member.Label); insert.Parameters.AddWithValue("$partition", member.Partition);
            insert.Parameters.AddWithValue("$group", member.LeakageGroup); insert.Parameters.AddWithValue("$source", (object?)member.SourceHash ?? DBNull.Value);
            insert.Parameters.AddWithValue("$embeddingHash", member.EmbeddingHash); insert.Parameters.AddWithValue("$quality", member.QualityScore);
            var bytes = new byte[member.Embedding.Length * sizeof(float)]; Buffer.BlockCopy(member.Embedding, 0, bytes, 0, bytes.Length);
            insert.Parameters.Add("$embedding", Microsoft.Data.Sqlite.SqliteType.Blob).Value = bytes; insert.ExecuteNonQuery();
        }
        tx.Commit();
        return new(snapshotId, DateTime.UtcNow, randomSeed, dataVersion, algorithmId, modelVersion, adapter, members.Count, train, validation, test);
    }

    public FaceEvaluationSnapshot? Get(string snapshotId)
    {
        using var command = repository.Database.Command("SELECT snapshot_id,created_utc,random_seed,data_version,algorithm_id,embedding_model_version,adapter_version,sample_count,train_count,validation_count,test_count FROM face_dataset_snapshots WHERE snapshot_id=$id");
        command.Parameters.AddWithValue("$id", snapshotId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind), reader.GetInt32(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10)) : null;
    }

    public IReadOnlyList<FaceEvaluationSnapshotMember> GetMembers(string snapshotId)
    {
        using var command = repository.Database.Command("SELECT sample_key,face_id,video_face_id,person_id,label,partition,leakage_group,source_hash,embedding_hash,quality_score,embedding FROM face_dataset_members WHERE snapshot_id=$id ORDER BY sample_key");
        command.Parameters.AddWithValue("$id", snapshotId);
        using var reader = command.ExecuteReader(); var result = new List<FaceEvaluationSnapshotMember>();
        while (reader.Read())
        {
            var bytes = (byte[])reader[10]; var embedding = new float[bytes.Length / sizeof(float)]; Buffer.BlockCopy(bytes, 0, embedding, 0, bytes.Length);
            result.Add(new(snapshotId, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8), reader.GetDouble(9), embedding));
        }
        return result;
    }

    private static FaceEvaluationSnapshotMember CreateMember(string key, long? faceId, long? videoFaceId, long personId,
        string label, string group, string sourceHash, float[] embedding, double quality, int seed) =>
        new("", key, faceId, videoFaceId, personId, label, Partition(group, seed), group, sourceHash,
            Convert.ToHexString(SHA256.HashData(ToBytes(embedding))), quality, embedding);

    private static string Partition(string group, int seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{group}"));
        var bucket = BitConverter.ToUInt32(hash, 0) % 100;
        return bucket < 70 ? "train" : bucket < 85 ? "validation" : "test";
    }

    private static double Quality(float confidence, float area) => Math.Clamp(confidence * .65 + Math.Min(1, area / .08f) * .35, .05, 1);
    private static byte[] ToBytes(float[] values) { var bytes = new byte[values.Length * sizeof(float)]; Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length); return bytes; }
}
