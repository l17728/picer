using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.Core.Vision;

public sealed record SFaceCalibrationResult(
    int ConfirmedPeople, int QualifiedPeople, int ConfirmedFaces, int SamePairs, int DifferentPairs,
    double RecommendedThreshold, double FalseAcceptRate, double FalseRejectRate, string ModelVersion);

public sealed record FaceDetectionGroundTruth(
    long ImageId, int ExpectedFaceCount, string Categories, string? Notes, DateTime UpdatedUtc);

public sealed record YuNetValidationRow(
    ImageRecord Image, int DetectedFaceCount, FaceDetectionGroundTruth? GroundTruth);

public sealed record YuNetValidationResult(
    int LabeledImages, int ExpectedFaces, int DetectedFaces, int MissedFaces, int ExtraDetections,
    double MissRate, double ExtraDetectionRate, IReadOnlyDictionary<string, (int Images, int MissedFaces)> Categories);

public sealed class FaceValidationService(PicerDatabase database, VisionAnalysisRepository repository)
{
    public double? GetAppliedSFaceThreshold(string modelVersion) => repository.GetFaceMatchThreshold(modelVersion);

    public void ApplySFaceThreshold(SFaceCalibrationResult calibration)
    {
        if (calibration.ModelVersion == "none" || calibration.SamePairs == 0 || calibration.DifferentPairs == 0)
            throw new InvalidOperationException("同人和异人样本对均存在时才能应用阈值。");
        repository.SaveFaceMatchThreshold(calibration.ModelVersion, calibration.RecommendedThreshold,
            calibration.SamePairs, calibration.DifferentPairs);
    }

    public SFaceCalibrationResult CalibrateSFace(int minimumPhotosPerPerson = 20, int maximumPairsPerClass = 200_000,
        IProgress<(string Stage, int Completed, int Total)>? progress = null)
    {
        progress?.Report(("加载已确认人脸", 0, 1));
        var confirmed = repository.GetEmbeddingsForMatching()
            .Where(face => face.AssignmentState == "confirmed" && face.PersonId is not null)
            .ToArray();
        var selectedVersion = confirmed.GroupBy(face => face.ModelVersion)
            .OrderByDescending(group => group.Count()).Select(group => group.Key).FirstOrDefault() ?? "none";
        var faces = confirmed.Where(face => face.ModelVersion == selectedVersion).ToArray();
        var groups = faces.GroupBy(face => face.PersonId!.Value).ToArray();
        var same = new List<double>();
        foreach (var group in groups)
        {
            var items = group.ToArray();
            for (var left = 0; left < items.Length && same.Count < maximumPairsPerClass; left++)
            for (var right = left + 1; right < items.Length && same.Count < maximumPairsPerClass; right++)
                if (items[left].ImageId != items[right].ImageId)
                {
                    same.Add(Cosine(items[left].Embedding, items[right].Embedding));
                    if (same.Count % 5000 == 0) progress?.Report(("生成同人样本对", same.Count, maximumPairsPerClass));
                }
        }

        var different = new List<double>();
        for (var leftGroup = 0; leftGroup < groups.Length && different.Count < maximumPairsPerClass; leftGroup++)
        for (var rightGroup = leftGroup + 1; rightGroup < groups.Length && different.Count < maximumPairsPerClass; rightGroup++)
        foreach (var left in groups[leftGroup])
        foreach (var right in groups[rightGroup])
        {
            if (different.Count >= maximumPairsPerClass) break;
            different.Add(Cosine(left.Embedding, right.Embedding));
            if (different.Count % 5000 == 0) progress?.Report(("生成异人样本对", different.Count, maximumPairsPerClass));
        }

        progress?.Report(("搜索最佳阈值", 0, 121));
        same.Sort();
        different.Sort();
        var bestThreshold = 0.62;
        var bestFar = RateAtOrAbove(different, bestThreshold);
        var bestFrr = RateBelow(same, bestThreshold);
        var bestLoss = bestFar + bestFrr;
        var thresholdIndex = 0;
        for (var threshold = 0.30; threshold <= 0.900001; threshold += 0.005)
        {
            var far = RateAtOrAbove(different, threshold);
            var frr = RateBelow(same, threshold);
            if (far + frr < bestLoss)
                (bestThreshold, bestFar, bestFrr, bestLoss) = (threshold, far, frr, far + frr);
            progress?.Report(("搜索最佳阈值", ++thresholdIndex, 121));
        }

        progress?.Report(("计算完成", 1, 1));

        return new(groups.Length,
            groups.Count(group => group.Select(face => face.ImageId).Distinct().Count() >= minimumPhotosPerPerson),
            faces.Length, same.Count, different.Count, bestThreshold, bestFar, bestFrr, selectedVersion);
    }

    private static double RateAtOrAbove(List<double> sorted, double threshold) => sorted.Count == 0
        ? 0 : (sorted.Count - LowerBound(sorted, threshold)) / (double)sorted.Count;

    private static double RateBelow(List<double> sorted, double threshold) => sorted.Count == 0
        ? 0 : LowerBound(sorted, threshold) / (double)sorted.Count;

    private static int LowerBound(List<double> sorted, double value)
    {
        var low = 0;
        var high = sorted.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (sorted[middle] < value) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    public IReadOnlyList<YuNetValidationRow> GetYuNetRows(int limit = 1000)
    {
        var labels = GetGroundTruth().ToDictionary(value => value.ImageId);
        return database.GetImages(includeMissing: false).Where(image => image.Status == ImageStatus.Active)
            .OrderBy(SceneTimeClusterService.EffectiveTime).ThenBy(image => image.Id).Take(Math.Clamp(limit, 1, 1000))
            .Select(image => new YuNetValidationRow(image, repository.GetFaceCount(image.Id), labels.GetValueOrDefault(image.Id)))
            .ToArray();
    }

    public void SaveGroundTruth(long imageId, int expectedFaceCount, string? categories, string? notes)
    {
        if (expectedFaceCount < 0) throw new ArgumentOutOfRangeException(nameof(expectedFaceCount));
        using var command = database.Command("""
            INSERT INTO face_detection_ground_truth(image_id,expected_face_count,categories,notes,updated_utc)
            VALUES($image,$expected,$categories,$notes,$now)
            ON CONFLICT(image_id) DO UPDATE SET expected_face_count=excluded.expected_face_count,
                categories=excluded.categories,notes=excluded.notes,updated_utc=excluded.updated_utc
            """);
        command.Parameters.AddWithValue("$image", imageId);
        command.Parameters.AddWithValue("$expected", expectedFaceCount);
        command.Parameters.AddWithValue("$categories", NormalizeCategories(categories));
        command.Parameters.AddWithValue("$notes", (object?)notes?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        command.ExecuteNonQuery();
    }

    public YuNetValidationResult EvaluateYuNet()
    {
        var rows = GetYuNetRows().Where(row => row.GroundTruth is not null).ToArray();
        var expected = rows.Sum(row => row.GroundTruth!.ExpectedFaceCount);
        var detected = rows.Sum(row => row.DetectedFaceCount);
        var missed = rows.Sum(row => Math.Max(0, row.GroundTruth!.ExpectedFaceCount - row.DetectedFaceCount));
        var extra = rows.Sum(row => Math.Max(0, row.DetectedFaceCount - row.GroundTruth!.ExpectedFaceCount));
        var categories = rows.SelectMany(row => SplitCategories(row.GroundTruth!.Categories).Select(category => (category, row)))
            .GroupBy(value => value.category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => (group.Count(), group.Sum(value => Math.Max(0, value.row.GroundTruth!.ExpectedFaceCount - value.row.DetectedFaceCount))),
                StringComparer.OrdinalIgnoreCase);
        return new(rows.Length, expected, detected, missed, extra,
            expected == 0 ? 0 : missed / (double)expected,
            expected == 0 ? 0 : extra / (double)expected, categories);
    }

    private IReadOnlyList<FaceDetectionGroundTruth> GetGroundTruth()
    {
        using var command = database.Command("SELECT image_id,expected_face_count,categories,notes,updated_utc FROM face_detection_ground_truth ORDER BY image_id");
        var values = new List<FaceDetectionGroundTruth>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) values.Add(new(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), DateTime.Parse(reader.GetString(4)).ToUniversalTime()));
        return values;
    }

    private static string NormalizeCategories(string? categories) => string.Join(',', SplitCategories(categories));
    private static IEnumerable<string> SplitCategories(string? categories) => (categories ?? "").Split([',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase);

    private static double Cosine(float[] left, float[] right)
    {
        var length = Math.Min(left.Length, right.Length);
        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var index = 0; index < length; index++)
        {
            dot += left[index] * right[index]; leftNorm += left[index] * left[index]; rightNorm += right[index] * right[index];
        }
        return length == 0 || leftNorm <= 0 || rightNorm <= 0 ? -1 : dot / Math.Sqrt(leftNorm * rightNorm);
    }
}
