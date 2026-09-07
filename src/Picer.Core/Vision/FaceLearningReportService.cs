using System.Text.Json;

namespace Picer.Core.Vision;

public sealed record FacePersonLearningReport(long PersonId, string Name, int ConfirmedPositive,
    int RejectedNegative, int Suggested, int PrototypeCount, double AverageQuality,
    string? EarliestModel, string? LatestModel, IReadOnlyList<string> Risks);

public sealed record FaceConfusionRisk(long PersonId, string PersonName, long ConfusedWithPersonId,
    string ConfusedWithName, int Count);

public sealed record FaceLearningReport(DateTime GeneratedUtc, string ReportVersion,
    int TotalPeople, int TotalConfirmedFaces, int TotalRejectedFaces, int TotalSuggestedFaces,
    IReadOnlyList<FacePersonLearningReport> People, IReadOnlyList<FaceConfusionRisk> Confusions,
    IReadOnlyList<string> GlobalRisks)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
}

/// <summary>Builds a read-only, algorithm-neutral report from human labels and embeddings.</summary>
public sealed class FaceLearningReportService(VisionAnalysisRepository repository)
{
    public FaceLearningReport Generate()
    {
        var people = new List<FacePersonLearningReport>();
        using var command = repository.Database.Command("""
            SELECT p.id,p.display_name,
              (SELECT COUNT(*) FROM face_person_assignments a WHERE a.person_id=p.id AND a.state='confirmed'),
              (SELECT COUNT(*) FROM face_person_rejections r WHERE r.person_id=p.id),
              (SELECT COUNT(*) FROM face_person_assignments a WHERE a.person_id=p.id AND a.state='suggested'),
              (SELECT COUNT(DISTINCT f.model_version) FROM detected_faces f JOIN face_person_assignments a ON a.face_id=f.id WHERE a.person_id=p.id AND a.state='confirmed' AND f.embedding IS NOT NULL),
              (SELECT COALESCE(AVG(f.confidence * .65 + MIN(1.0, f.box_width*f.box_height/.08) * .35),0) FROM detected_faces f JOIN face_person_assignments a ON a.face_id=f.id WHERE a.person_id=p.id AND a.state='confirmed' AND f.embedding IS NOT NULL),
              (SELECT MIN(f.model_version) FROM detected_faces f JOIN face_person_assignments a ON a.face_id=f.id WHERE a.person_id=p.id AND a.state='confirmed' AND f.embedding IS NOT NULL),
              (SELECT MAX(f.model_version) FROM detected_faces f JOIN face_person_assignments a ON a.face_id=f.id WHERE a.person_id=p.id AND a.state='confirmed' AND f.embedding IS NOT NULL)
            FROM people p ORDER BY p.display_name COLLATE NOCASE
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var positive = reader.GetInt32(2); var negative = reader.GetInt32(3); var suggested = reader.GetInt32(4);
            var risks = new List<string>();
            if (positive < 20) risks.Add("正样本不足（少于 20）");
            if (negative < 10) risks.Add("排他负样本不足（少于 10）");
            if (reader.GetInt32(5) == 0) risks.Add("没有可用于原型的向量");
            if (reader.GetDouble(6) < .4 && positive > 0) risks.Add("样本质量偏低");
            people.Add(new(reader.GetInt64(0), reader.GetString(1), positive, negative, suggested,
                reader.GetInt32(5), reader.GetDouble(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), risks));
        }

        var names = people.ToDictionary(x => x.PersonId, x => x.Name);
        var confusions = new List<FaceConfusionRisk>();
        using (var confusion = repository.Database.Command("""
            SELECT a.person_id,r.person_id,COUNT(*)
            FROM face_person_assignments a JOIN face_person_rejections r ON r.face_id=a.face_id
            WHERE a.state='confirmed' GROUP BY a.person_id,r.person_id HAVING COUNT(*)>0 ORDER BY COUNT(*) DESC
            """))
        using (var rows = confusion.ExecuteReader())
            while (rows.Read() && names.TryGetValue(rows.GetInt64(0), out var left) && names.TryGetValue(rows.GetInt64(1), out var right))
                confusions.Add(new(rows.GetInt64(0), left, rows.GetInt64(1), right, rows.GetInt32(2)));

        var global = new List<string>();
        if (people.Count == 0) global.Add("尚未建立已命名人物");
        if (people.Any(x => x.PrototypeCount == 0)) global.Add("存在没有向量原型的人物");
        if (people.Any(x => x.ConfirmedPositive < 5)) global.Add("至少一位人物的正样本不足以进行可靠评估");
        return new(DateTime.UtcNow, "face-report-v1", people.Count, people.Sum(x => x.ConfirmedPositive),
            people.Sum(x => x.RejectedNegative), people.Sum(x => x.Suggested), people, confusions, global);
    }
}
