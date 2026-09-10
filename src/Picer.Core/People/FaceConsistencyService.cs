using System.Text;
using Picer.Core.Data;

namespace Picer.Core.People;

public sealed record FaceConsistencyIssue(long ImageId, long FaceId, long? PersonId, string IssueType, string Description);

public sealed record FaceConsistencyReport(int ScannedImages, int ScannedFaces, IReadOnlyList<FaceConsistencyIssue> Issues, int Repaired);

public static class FaceConsistencyService
{
    public static FaceConsistencyReport ScanAndRepair(PicerDatabase db, bool repair = true)
    {
        var issues = new List<FaceConsistencyIssue>();
        int scannedImages, scannedFaces, repaired = 0;

        using (var countCmd = db.Command("SELECT COUNT(DISTINCT image_id) FROM detected_faces"))
            scannedImages = Convert.ToInt32(countCmd.ExecuteScalar());
        using (var countCmd = db.Command("SELECT COUNT(*) FROM detected_faces"))
            scannedFaces = Convert.ToInt32(countCmd.ExecuteScalar());

        var personNames = new Dictionary<long, string>();
        using (var cmd = db.Command("SELECT id, display_name FROM people"))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                personNames[reader.GetInt64(0)] = reader.GetString(1);

        var assignmentsByFace = new Dictionary<long, (long PersonId, string State)>();
        using (var cmd = db.Command("SELECT face_id, person_id, state FROM face_person_assignments"))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                assignmentsByFace[reader.GetInt64(0)] = (reader.GetInt64(1), reader.GetString(2));

        var faceImages = new Dictionary<long, long>();
        using (var cmd = db.Command("SELECT id, image_id FROM detected_faces"))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                faceImages[reader.GetInt64(0)] = reader.GetInt64(1);

        var orphanedAssignments = assignmentsByFace.Keys.Where(faceId => !faceImages.ContainsKey(faceId)).ToList();
        foreach (var faceId in orphanedAssignments)
        {
            var assignment = assignmentsByFace[faceId];
            issues.Add(new FaceConsistencyIssue(0, faceId, assignment.PersonId, "orphaned_assignment",
                $"人脸 {faceId} 已不存在但归属记录仍指向人物 {assignment.PersonId}"));
            if (repair)
            {
                using var cmd = db.Command("DELETE FROM face_person_assignments WHERE face_id = $f");
                cmd.Parameters.AddWithValue("$f", faceId);
                cmd.ExecuteNonQuery();
                repaired++;
            }
        }

        var confirmedByImage = new Dictionary<long, List<(long FaceId, long PersonId)>>();
        foreach (var (faceId, (personId, state)) in assignmentsByFace)
        {
            if (state != "confirmed" || !faceImages.TryGetValue(faceId, out var imageId)) continue;
            if (!confirmedByImage.TryGetValue(imageId, out var list))
                confirmedByImage[imageId] = list = [];
            list.Add((faceId, personId));
        }

        foreach (var (imageId, faces) in confirmedByImage)
        {
            var names = faces.Select(f => personNames.GetValueOrDefault(f.PersonId, $"人物#{f.PersonId}"))
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct()
                .OrderBy(n => n)
                .ToList();
            if (names.Count == 0) continue;
            var expectedTag = "人物：" + string.Join("、", names);

            string? existingDescription = null;
            using (var cmd = db.Command("SELECT COALESCE(summary,'') FROM cloud_vision_analyses WHERE image_id = $i AND status='completed' ORDER BY updated_utc DESC LIMIT 1"))
            {
                cmd.Parameters.AddWithValue("$i", imageId);
                existingDescription = Convert.ToString(cmd.ExecuteScalar());
            }

            if (string.IsNullOrEmpty(existingDescription) || !existingDescription.Contains(expectedTag))
            {
                issues.Add(new FaceConsistencyIssue(imageId, faces[0].FaceId, faces[0].PersonId, "description_stale",
                    $"照片 {imageId} 有 {faces.Count} 张已确认人脸（{expectedTag}）但文字索引未包含最新人物标记"));
                if (repair)
                {
                    var textIndex = new Picer.Core.Search.MediaTextIndexRepository(db);
                    var record = db.GetImage(imageId);
                    if (record is not null)
                    {
                        var current = existingDescription ?? "";
                        var updated = string.IsNullOrEmpty(current) ? expectedTag : $"{expectedTag}。{current}";
                        textIndex.UpsertImage(record, [record.Path, record.Camera, record.Gps, updated, expectedTag], "consistency:v1");
                        repaired++;
                    }
                }
            }
        }

        return new FaceConsistencyReport(scannedImages, scannedFaces, issues, repaired);
    }

    public static string BuildTrainingSummary(PicerDatabase db)
    {
        var falseMerges = FaceActionLogService.GetFalseMergePairs(db);
        var confirms = FaceActionLogService.GetConfirmedPairs(db);
        var allActions = FaceActionLogService.GetActions(db, limit: 1000);

        var sb = new StringBuilder();
        sb.AppendLine("== 人脸训练审计摘要 ==");
        sb.AppendLine($"记录动作总数：{allActions.Count}");
        sb.AppendLine($"错误合并对（不同人物被聚为一组）：{falseMerges.Count}");
        sb.AppendLine($"确认正对（同人物确认）：{confirms.Count}");
        sb.AppendLine();

        if (falseMerges.Count > 0)
        {
            sb.AppendLine("== 错误合并对清单（训练困难负样本）==");
            foreach (var pair in falseMerges.Take(50))
                sb.AppendLine($"  人脸 {pair.FaceA}（人物 {pair.PersonA}）≠ 人脸 {pair.FaceB}（人物 {pair.PersonB}）| 置信度 {pair.ClusterConfidence:F2} | {pair.Reason}");
        }

        if (confirms.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("== 确认正对清单（训练正样本）==");
            foreach (var pair in confirms.Take(50))
                sb.AppendLine($"  人脸 {pair.FaceA} = 人脸 {pair.FaceB}（人物 {pair.PersonA}）| 置信度 {pair.ClusterConfidence:F2}");
        }

        return sb.ToString();
    }
}
