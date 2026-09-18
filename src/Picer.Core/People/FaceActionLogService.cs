using System.Text.Json;
using Picer.Core.Data;

namespace Picer.Core.People;

public sealed record FaceActionEntry(
    long Id,
    DateTime CreatedUtc,
    string Action,
    long FaceId,
    long? PersonId,
    long? SourcePersonId,
    IReadOnlyList<long> CoClusteredFaceIds,
    IReadOnlyList<long> CoClusteredPersonIds,
    double? ClusterConfidence,
    string ModelVersion,
    string? ErrorType);

public sealed record FaceTrainingPair(
    long FaceA,
    long FaceB,
    long? PersonA,
    long? PersonB,
    string Reason,
    double? ClusterConfidence,
    string ModelVersion,
    DateTime RecordedUtc);

public static class FaceActionLogService
{
    public static void Record(PicerDatabase db, string action, long faceId, long? personId,
        long? sourcePersonId, IReadOnlyList<long> coFaceIds, IReadOnlyList<long> coPersonIds,
        double? clusterConfidence, string modelVersion, string? errorType = null)
    {
        using var cmd = db.Command("""
            INSERT INTO face_action_log (created_utc, action, face_id, person_id, source_person_id,
                co_clustered_face_ids, co_clustered_person_ids, cluster_confidence, model_version, error_type)
            VALUES ($t, $a, $f, $p, $sp, $cf, $cp, $cc, $mv, $e)
            """);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$a", action);
        cmd.Parameters.AddWithValue("$f", faceId);
        cmd.Parameters.AddWithValue("$p", (object?)personId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sp", (object?)sourcePersonId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cf", JsonSerializer.Serialize(coFaceIds));
        cmd.Parameters.AddWithValue("$cp", JsonSerializer.Serialize(coPersonIds));
        cmd.Parameters.AddWithValue("$cc", (object?)clusterConfidence ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mv", modelVersion);
        cmd.Parameters.AddWithValue("$e", (object?)errorType ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public static void MarkErrorType(PicerDatabase db, long logId, string errorType)
    {
        using var cmd = db.Command("UPDATE face_action_log SET error_type = $e WHERE id = $id");
        cmd.Parameters.AddWithValue("$e", errorType);
        cmd.Parameters.AddWithValue("$id", logId);
        cmd.ExecuteNonQuery();
    }

    public static List<FaceActionEntry> GetActions(PicerDatabase db, string? actionFilter = null, int limit = 500)
    {
        var sql = "SELECT id, created_utc, action, face_id, person_id, source_person_id, co_clustered_face_ids, co_clustered_person_ids, cluster_confidence, model_version, error_type FROM face_action_log";
        if (!string.IsNullOrEmpty(actionFilter)) sql += " WHERE action = $a";
        sql += " ORDER BY created_utc DESC LIMIT $lim";
        using var cmd = db.Command(sql);
        if (!string.IsNullOrEmpty(actionFilter)) cmd.Parameters.AddWithValue("$a", actionFilter);
        cmd.Parameters.AddWithValue("$lim", limit);
        using var reader = cmd.ExecuteReader();
        var results = new List<FaceActionEntry>();
        while (reader.Read())
            results.Add(new FaceActionEntry(
                reader.GetInt64(0),
                DateTime.Parse(reader.GetString(1)).ToUniversalTime(),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                JsonSerializer.Deserialize<long[]>(reader.GetString(6)) ?? [],
                JsonSerializer.Deserialize<long[]>(reader.GetString(7)) ?? [],
                reader.IsDBNull(8) ? null : reader.GetDouble(8),
                reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        return results;
    }

    public static List<FaceTrainingPair> GetFalseMergePairs(PicerDatabase db)
    {
        var rejections = GetActions(db, "reject");
        var moves = GetActions(db, "move_out").Concat(GetActions(db, "reclassify")).ToList();
        var pairs = new List<FaceTrainingPair>();

        foreach (var entry in rejections.Concat(moves))
        {
            var coFaces = entry.CoClusteredFaceIds;
            var coPersons = entry.CoClusteredPersonIds;
            for (var i = 0; i < coFaces.Count && i < coPersons.Count; i++)
            {
                if (coPersons[i] == 0) continue;
                var targetPerson = entry.PersonId ?? 0;
                if (coPersons[i] != targetPerson || entry.Action == "reject")
                    pairs.Add(new FaceTrainingPair(
                        entry.FaceId, coFaces[i],
                        entry.PersonId, coPersons[i],
                        entry.Action == "reject"
                            ? $"用户拒绝人脸 {entry.FaceId} 归入候选组（同组已确认人物 {coPersons[i]}）"
                            : $"用户将人脸 {entry.FaceId} 移出（原组内已确认人物 {coPersons[i]}，目标人物 {entry.PersonId}）",
                        entry.ClusterConfidence,
                        entry.ModelVersion,
                        entry.CreatedUtc));
            }
        }
        return pairs;
    }

    public static List<FaceTrainingPair> GetConfirmedPairs(PicerDatabase db)
    {
        var confirmations = GetActions(db, "confirm");
        var pairs = new List<FaceTrainingPair>();
        foreach (var entry in confirmations)
        {
            var coFaces = entry.CoClusteredFaceIds;
            var coPersons = entry.CoClusteredPersonIds;
            for (var i = 0; i < coFaces.Count && i < coPersons.Count; i++)
            {
                if (coPersons[i] == 0 || coPersons[i] != entry.PersonId) continue;
                pairs.Add(new FaceTrainingPair(
                    entry.FaceId, coFaces[i],
                    entry.PersonId, coPersons[i],
                    $"用户确认人脸 {entry.FaceId} 与 {coFaces[i]} 同属人物 {entry.PersonId}",
                    entry.ClusterConfidence,
                    entry.ModelVersion,
                    entry.CreatedUtc));
            }
        }
        return pairs;
    }
}
