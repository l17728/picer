namespace Picer.Core.Vision;

public sealed record FaceContextQuery(DateTime? CaptureTimeUtc = null, string? Location = null,
    string? Album = null, string? Scene = null, IReadOnlySet<long>? CoPeople = null);

public sealed record FaceContextCandidate(long FaceId, long PersonId, double VectorScore,
    double RequiredThreshold, DateTime? CaptureTimeUtc = null, string? Location = null,
    string? Album = null, string? Scene = null, IReadOnlySet<long>? PeopleInMedia = null)
{
    public bool VectorAccepted => VectorScore >= RequiredThreshold;
}

public sealed record FaceContextRankedCandidate(FaceContextCandidate Candidate, double Score,
    IReadOnlyList<string> Evidence);

/// <summary>Adds bounded, explainable context boosts only after vector identity eligibility.</summary>
public static class FaceContextRankingService
{
    public static IReadOnlyList<FaceContextRankedCandidate> Rank(IEnumerable<FaceContextCandidate> candidates,
        FaceContextQuery query)
    {
        ArgumentNullException.ThrowIfNull(candidates); ArgumentNullException.ThrowIfNull(query);
        return candidates.Select(candidate =>
        {
            var evidence = new List<string>(); var boost = 0d;
            if (query.CaptureTimeUtc is DateTime time && candidate.CaptureTimeUtc is DateTime capture)
            {
                var days = Math.Abs((capture - time).TotalDays);
                if (days <= 1) { boost += .02; evidence.Add("时间接近"); }
                else if (days <= 7) { boost += .01; evidence.Add("时间同周"); }
            }
            if (Matches(query.Location, candidate.Location)) { boost += .02; evidence.Add("地点一致"); }
            if (Matches(query.Album, candidate.Album)) { boost += .015; evidence.Add("相册一致"); }
            if (Matches(query.Scene, candidate.Scene)) { boost += .015; evidence.Add("场景一致"); }
            if (query.CoPeople is { Count: > 0 } && candidate.PeopleInMedia is { Count: > 0 } && query.CoPeople.Overlaps(candidate.PeopleInMedia))
            { boost += .01; evidence.Add("共同出现人物"); }
            boost = Math.Min(.08, boost);
            return new FaceContextRankedCandidate(candidate, candidate.VectorScore + boost, evidence);
        }).OrderByDescending(x => x.Candidate.VectorAccepted)
          .ThenByDescending(x => x.Score).ThenBy(x => x.Candidate.FaceId).ToArray();
    }

    private static bool Matches(string? query, string? value) => !string.IsNullOrWhiteSpace(query) &&
        !string.IsNullOrWhiteSpace(value) && value.Contains(query, StringComparison.OrdinalIgnoreCase);
}
