namespace Picer.ModelServer;

/// <summary>Holdout metrics; never substitutes training or test data for validation.</summary>
public static class TrainingMetricsEvaluator
{
    public static TrainingMetrics Evaluate(IReadOnlyList<PersonPrototype> prototypes,
        IReadOnlyList<FaceSample> samples, IReadOnlyList<FaceNegativeSample> negatives)
    {
        var train = samples.Where(s => s.Split == "train" && s.Label != "negative").ToArray();
        var validation = samples.Where(s => s.Split == "validation").ToArray();
        if (prototypes.Count == 0 || train.Length == 0 || !validation.Any(s => s.Label != "negative"))
            throw new InvalidOperationException("需要独立的训练集和正样本验证集");
        long Predict(float[] vector)
        {
            var best = prototypes.Select(p => (Prototype: p, Score: Cosine(vector, p.Centroid)))
                .MaxBy(p => p.Score);
            return best.Score >= best.Prototype.Threshold ? best.Prototype.PersonId : 0;
        }
        var correct = 0;
        var missed = 0;
        var wronglyIdentified = 0;
        var falseAccepts = 0;
        var negativeCount = 0;
        foreach (var sample in validation)
        {
            var predicted = Predict(sample.Embedding!);
            if (sample.Label == "negative")
            {
                negativeCount++;
                if (predicted != 0) falseAccepts++; else correct++;
            }
            else if (predicted == sample.PersonId) correct++;
            else { missed++; if (predicted != 0) wronglyIdentified++; }
        }
        foreach (var negative in negatives.Where(n => n.Split == "validation"))
        {
            negativeCount++;
            // Exclusive labels reject this target identity, not every known person.
            var candidates = negative.Kind == "exclusive"
                ? prototypes.Where(p => p.PersonId == negative.TargetPersonId) : prototypes;
            if (candidates.Any(p => Cosine(negative.Embedding, p.Centroid) >= p.Threshold)) falseAccepts++;
        }
        var positives = validation.Count(s => s.Label != "negative");
        var far = negativeCount == 0 ? 1.0 : (double)falseAccepts / negativeCount;
        // A wrong accepted identity must also prevent a misleadingly perfect FAR.
        far = Math.Max(far, (double)wronglyIdentified / positives);
        return new TrainingMetrics(samples.Count + negatives.Count, prototypes.Count,
            (double)train.Count(s => Predict(s.Embedding!) == s.PersonId) / train.Length,
            (double)correct / validation.Length, far, (double)missed / positives, 0, 0);
    }

    private static double Cosine(float[] left, float[] right)
    {
        if (left is null || left.Length == 0 || left.Length != right.Length)
            throw new InvalidOperationException("向量维数不一致");
        double dot = 0, a = 0, b = 0;
        for (var i = 0; i < left.Length; i++)
        {
            if (!float.IsFinite(left[i]) || !float.IsFinite(right[i]))
                throw new InvalidOperationException("向量包含非有限值");
            dot += left[i] * (double)right[i]; a += left[i] * (double)left[i]; b += right[i] * (double)right[i];
        }
        if (a == 0 || b == 0) throw new InvalidOperationException("向量不能为零");
        return dot / Math.Sqrt(a * b);
    }
}
