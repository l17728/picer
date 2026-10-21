namespace Picer.Core.Search;

public sealed record RankedCandidate(long MediaId, int Rank);
public sealed record ReciprocalRankHit(long MediaId, double Score,
    IReadOnlyDictionary<string, int> SourceRanks);

public static class ReciprocalRankFusion
{
    public static IReadOnlyList<ReciprocalRankHit> Fuse(
        IEnumerable<(string Source, IEnumerable<RankedCandidate> Candidates)> sources,
        int count, int rankConstant = 60, CancellationToken cancellationToken = default)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        if (rankConstant < 1) throw new ArgumentOutOfRangeException(nameof(rankConstant));
        var ranks = new Dictionary<long, Dictionary<string, int>>();
        foreach (var (source, candidates) in sources)
        {
            if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("RRF source is required.", nameof(sources));
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate.Rank < 1) throw new ArgumentOutOfRangeException(nameof(sources), "Ranks are one-based.");
                if (!ranks.TryGetValue(candidate.MediaId, out var bySource))
                    ranks[candidate.MediaId] = bySource = new(StringComparer.Ordinal);
                if (!bySource.TryGetValue(source, out var existing) || candidate.Rank < existing)
                    bySource[source] = candidate.Rank;
            }
        }
        return ranks.Select(item => new ReciprocalRankHit(item.Key,
                item.Value.Values.Sum(rank => 1d / (rankConstant + rank)), item.Value))
            .OrderByDescending(hit => hit.Score).ThenBy(hit => hit.MediaId).Take(count).ToArray();
    }
}
