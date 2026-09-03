using Picer.Core.People;

namespace Picer.Core.Vision;

public sealed record FaceSuggestionResult(int Considered, int Suggested, int BelowThreshold, double AppliedThreshold);

/// <summary>Suggests matches against user-confirmed people; it never confirms an identity.</summary>
public sealed class FaceSuggestionEngine(VisionAnalysisRepository repository, PeopleService people)
{
    public FaceSuggestionResult Suggest(double? cosineThreshold = null)
    {
        if (cosineThreshold is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(cosineThreshold));
        var embeddings = repository.GetEmbeddingsForMatching();
        var selectedVersion = embeddings.Where(face => face.AssignmentState == "confirmed")
            .GroupBy(face => face.ModelVersion).OrderByDescending(group => group.Count())
            .Select(group => group.Key).FirstOrDefault();
        var appliedThreshold = cosineThreshold ?? (selectedVersion is null ? null : repository.GetFaceMatchThreshold(selectedVersion)) ?? 0.55;
        var confirmed = embeddings
            .Where(face => face.AssignmentState == "confirmed" && face.PersonId is not null)
            .Select(face => (PersonId: face.PersonId!.Value, face.Embedding))
            .Concat(people.GetConfirmedVideoFaceEmbeddings().Select(face => (face.PersonId, face.Embedding)));
        var prototypes = confirmed
            .GroupBy(face => face.PersonId)
            .ToDictionary(group => group.Key, group => Mean(group.Select(face => face.Embedding)));
        var candidates = embeddings.Where(face => face.AssignmentState is null or "suggested").ToArray();
        var suggested = 0;
        var below = 0;

        foreach (var face in candidates)
        {
            var match = prototypes
                .Select(pair => (PersonId: pair.Key, Score: Cosine(face.Embedding, pair.Value)))
                .OrderByDescending(value => value.Score)
                .FirstOrDefault();
            if (match.Score < appliedThreshold)
            {
                below++;
                continue;
            }
            if (people.SuggestFace(face.FaceId, match.PersonId, match.Score)) suggested++;
        }
        return new FaceSuggestionResult(candidates.Length, suggested, below, appliedThreshold);
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
}
