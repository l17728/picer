namespace Picer.ModelServer;

public sealed record RawBaselineMetrics(int TotalSamples, int GallerySamples, int QuerySamples,
    int Persons, double Accuracy, double Precision, double Recall, double FalseAcceptRate,
    double FalseRejectRate);

public static class RawFaceBaselineEvaluator
{
    public static RawBaselineMetrics Evaluate(FaceSample[] samples)
    {
        var gallery = samples.Where(sample => string.Equals(sample.Split, "train", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sample.Split, "gallery", StringComparison.OrdinalIgnoreCase))
            .Where(sample => sample.Embedding is { Length: > 0 }).ToArray();
        var queries = samples.Where(sample => string.Equals(sample.Split, "validation", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sample.Split, "test", StringComparison.OrdinalIgnoreCase))
            .Where(sample => sample.Embedding is { Length: > 0 }).ToArray();
        if (gallery.Length == 0 || queries.Length == 0)
            throw new InvalidOperationException("基线评估需要 gallery(train) 和 validation/test 查询样本");
        var prototypes = gallery.GroupBy(sample => sample.PersonId).ToDictionary(group => group.Key,
            group => Centroid(group.Select(sample => sample.Embedding!).ToArray()));
        var correct = 0;
        foreach (var query in queries)
        {
            var predicted = prototypes.Select(pair => (PersonId: pair.Key, Score: Cosine(query.Embedding!, pair.Value)))
                .OrderByDescending(value => value.Score).First().PersonId;
            if (predicted == query.PersonId) correct++;
        }
        var accuracy = (double)correct / queries.Length;
        return new RawBaselineMetrics(samples.Length, gallery.Length, queries.Length, prototypes.Count,
            accuracy, accuracy, accuracy, 1 - accuracy, 1 - accuracy);
    }

    private static float[] Centroid(float[][] values)
    {
        var result = new float[values[0].Length];
        foreach (var value in values)
            for (var i = 0; i < result.Length; i++) result[i] += value[i];
        for (var i = 0; i < result.Length; i++) result[i] /= values.Length;
        return result;
    }

    private static double Cosine(float[] left, float[] right)
    {
        double dot = 0, leftNorm = 0, rightNorm = 0;
        var length = Math.Min(left.Length, right.Length);
        for (var i = 0; i < length; i++) { dot += left[i] * right[i]; leftNorm += left[i] * left[i]; rightNorm += right[i] * right[i]; }
        return dot / Math.Max(1e-12, Math.Sqrt(leftNorm * rightNorm));
    }
}
