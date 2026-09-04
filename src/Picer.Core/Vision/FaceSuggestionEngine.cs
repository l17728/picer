using Picer.Core.People;

namespace Picer.Core.Vision;

public sealed record FaceSuggestionResult(int Considered, int Suggested, int BelowThreshold, double AppliedThreshold,
    int LowQualityDeferred = 0, int AmbiguousDeferred = 0, int NegativeConflictDeferred = 0,
    int MultiPrototypePeople = 0, int NegativeSamples = 0);

/// <summary>Suggests matches against user-confirmed people; it never confirms an identity.</summary>
public sealed class FaceSuggestionEngine(VisionAnalysisRepository repository, PeopleService people)
{
    public FaceSuggestionResult Suggest(double? cosineThreshold = null,
        IProgress<(int Completed, int Total)>? progress = null)
    {
        if (cosineThreshold is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(cosineThreshold));
        var embeddings = repository.GetEmbeddingsForMatching();
        var selectedVersion = embeddings.Where(face => face.AssignmentState == "confirmed")
            .GroupBy(face => face.ModelVersion).OrderByDescending(group => group.Count())
            .Select(group => group.Key).FirstOrDefault();
        var appliedThreshold = cosineThreshold ?? (selectedVersion is null ? null : repository.GetFaceMatchThreshold(selectedVersion)) ?? 0.55;
        var adapter = selectedVersion is null ? null : repository.GetActiveFaceAdapter(selectedVersion);
        var confirmed = embeddings
            .Where(face => face.AssignmentState == "confirmed" && face.PersonId is not null)
            .Select(face => (PersonId: face.PersonId!.Value, Embedding: ApplyAdapter(face.Embedding, adapter)))
            .Concat(people.GetConfirmedVideoFaceEmbeddings().Select(face =>
                (PersonId: face.PersonId, Embedding: ApplyAdapter(face.Embedding, adapter))));
        var prototypes = confirmed
            .GroupBy(face => face.PersonId)
            .ToDictionary(group => group.Key, group => BuildPrototypes(group.Select(face => face.Embedding)));
        var learnedPolicies = selectedVersion is null
            ? new Dictionary<long, FaceLearnedPolicy>()
            : repository.GetLearnedFacePolicies(selectedVersion).ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var policy in learnedPolicies.Values)
        {
            if (prototypes.TryGetValue(policy.PersonId, out var existing))
                prototypes[policy.PersonId] = existing.Concat([policy.Prototype]).ToArray();
        }
        var negativePrototypes = repository.GetRejectedFaceEmbeddings()
            .GroupBy(face => face.PersonId)
            .ToDictionary(group => group.Key, group => BuildPrototypes(group.Select(face => ApplyAdapter(face.Embedding, adapter)), 12));
        var adaptiveThresholds = BuildAdaptiveThresholds(appliedThreshold, prototypes, negativePrototypes);
        foreach (var policy in learnedPolicies.Values)
            adaptiveThresholds[policy.PersonId] = Math.Max(appliedThreshold, policy.MatchThreshold);
        var candidates = embeddings.Where(face => face.AssignmentState is null or "suggested").ToArray();
        var suggested = 0;
        var below = 0;
        var lowQualityDeferred = 0;
        var ambiguousDeferred = 0;
        var negativeConflictDeferred = 0;
        var staleSuggestionIds = new List<long>();

        for (var index = 0; index < candidates.Length; index++)
        {
            var face = candidates[index];
            var candidateEmbedding = ApplyAdapter(face.Embedding, adapter);
            var matches = prototypes
                .Select(pair => (PersonId: pair.Key, Score: pair.Value.Max(prototype => Cosine(candidateEmbedding, prototype))))
                .OrderByDescending(value => value.Score)
                .Take(2).ToArray();
            var match = matches.FirstOrDefault();
            var secondBestScore = matches.Length > 1 ? matches[1].Score : -1;
            var requiredThreshold = QualityAdjustedThreshold(appliedThreshold, face);
            var rejectedSimilarity = negativePrototypes.TryGetValue(match.PersonId, out var negatives)
                ? negatives.Max(prototype => Cosine(candidateEmbedding, prototype)) : -1;
            var adaptiveThreshold = adaptiveThresholds.GetValueOrDefault(match.PersonId, appliedThreshold);
            var deferred = false;
            if (match.Score < appliedThreshold)
            {
                below++;
                deferred = true;
            }
            else if (match.Score < requiredThreshold) { lowQualityDeferred++; deferred = true; }
            else if (match.Score - secondBestScore < 0.025) { ambiguousDeferred++; deferred = true; }
            else if (match.Score < adaptiveThreshold || match.Score - rejectedSimilarity < 0.03)
            { negativeConflictDeferred++; deferred = true; }

            if (deferred)
            {
                if (face.AssignmentState == "suggested") staleSuggestionIds.Add(face.FaceId);
            }
            else if (!(face.AssignmentState == "suggested" && face.PersonId == match.PersonId) &&
                     people.SuggestFace(face.FaceId, match.PersonId, match.Score)) suggested++;
            if ((index + 1) % 100 == 0 || index + 1 == candidates.Length)
                progress?.Report((index + 1, candidates.Length));
        }
        people.ClearAutomaticSuggestions(staleSuggestionIds);
        return new FaceSuggestionResult(candidates.Length, suggested, below, appliedThreshold,
            lowQualityDeferred, ambiguousDeferred, negativeConflictDeferred,
            prototypes.Count(pair => pair.Value.Count > 1), negativePrototypes.Sum(pair => pair.Value.Count));
    }

    private static float[] Mean(IEnumerable<float[]> vectors)
    {
        var items = vectors.Where(vector => vector.Length > 0).ToArray();
        if (items.Length == 0) return [];
        var length = items.Min(vector => vector.Length);
        var result = new float[length];
        foreach (var vector in items)
            for (var index = 0; index < length; index++) result[index] += vector[index];
        for (var index = 0; index < result.Length; index++) result[index] /= items.Length;
        var norm = Math.Sqrt(result.Sum(value => value * value));
        if (norm > double.Epsilon)
            for (var index = 0; index < result.Length; index++) result[index] = (float)(result[index] / norm);
        return result;
    }

    private static IReadOnlyList<float[]> BuildPrototypes(IEnumerable<float[]> vectors, int maximum = 8)
    {
        var samples = vectors.Where(vector => vector.Length > 0).ToArray();
        if (samples.Length == 0) return [];
        var mean = Mean(samples);
        var prototypes = new List<float[]>
        {
            samples.OrderByDescending(sample => Cosine(sample, mean)).First()
        };
        while (prototypes.Count < maximum)
        {
            var candidate = samples
                .Select(sample => (Sample: sample, Similarity: prototypes.Max(prototype => Cosine(sample, prototype))))
                .OrderBy(value => value.Similarity).First();
            if (candidate.Similarity >= 0.92) break;
            prototypes.Add(candidate.Sample);
        }
        return prototypes;
    }

    private static double QualityAdjustedThreshold(double baseline, FaceEmbeddingRecord face)
    {
        var adjustment = 0d;
        if (face.DetectionConfidence < 0.85f) adjustment += 0.04;
        if (face.RelativeArea < 0.01f) adjustment += 0.05;
        else if (face.RelativeArea < 0.025f) adjustment += 0.03;
        return Math.Min(0.98, baseline + adjustment);
    }

    private static Dictionary<long, double> BuildAdaptiveThresholds(double baseline,
        IReadOnlyDictionary<long, IReadOnlyList<float[]>> positives,
        IReadOnlyDictionary<long, IReadOnlyList<float[]>> negatives)
    {
        var result = new Dictionary<long, double>();
        foreach (var (personId, negativeSamples) in negatives)
        {
            if (negativeSamples.Count < 3 || !positives.TryGetValue(personId, out var positivePrototypes)) continue;
            var scores = negativeSamples.Select(sample =>
                    positivePrototypes.Max(prototype => Cosine(sample, prototype)))
                .OrderBy(score => score).ToArray();
            var percentileIndex = Math.Clamp((int)Math.Ceiling(scores.Length * 0.95) - 1, 0, scores.Length - 1);
            result[personId] = Math.Min(0.98, Math.Max(baseline, scores[percentileIndex] + 0.03));
        }
        return result;
    }

    private static double Cosine(float[] left, float[] right)
    {
        var length = Math.Min(left.Length, right.Length);
        if (length == 0) return -1;
        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var index = 0; index < length; index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }
        return leftNorm <= 0 || rightNorm <= 0 ? -1 : dot / Math.Sqrt(leftNorm * rightNorm);
    }

    private static float[] ApplyAdapter(float[] vector, float[]? weights)
    {
        if (weights is null || weights.Length != vector.Length) return vector;
        var result = new float[vector.Length];
        double norm = 0;
        for (var index = 0; index < vector.Length; index++)
        {
            result[index] = vector[index] * weights[index];
            norm += result[index] * result[index];
        }
        if (norm <= 0) return vector;
        var scale = 1 / Math.Sqrt(norm);
        for (var index = 0; index < result.Length; index++) result[index] = (float)(result[index] * scale);
        return result;
    }
}
