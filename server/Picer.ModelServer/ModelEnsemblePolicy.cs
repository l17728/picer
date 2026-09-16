namespace Picer.ModelServer;

public sealed record ModelEnsembleMember(string ModelVersion, string AlgorithmId, double Weight, string Role);
public sealed record ModelEnsembleScore(string ModelVersion, long PersonId, double Similarity, double Threshold, bool Accepted);
public sealed record ModelEnsembleDecision(string Decision, long? PersonId, double WeightedScore,
    int AcceptedVotes, int TotalVotes, string Reason);

public static class ModelEnsemblePolicy
{
    public static IReadOnlyList<string> Route(string? scenario) =>
        scenario?.ToLowerInvariant() switch
        {
            "侧脸" or "profile" or "逆光" or "backlit" or "遮挡" or "occluded" => ["insightface-arcface", "adaface", "arcface-paddle"],
            "小脸" or "small-face" or "低清" or "low-quality" => ["insightface-arcface", "arcface-paddle", "sface"],
            "视频" or "video" => ["sface", "arcface-paddle"],
            _ => ["sface", "insightface-arcface"]
        };

    public static IReadOnlyList<ModelEnsembleMember> Build(IReadOnlyList<ModelCandidateRanking> rankings)
    {
        var retained = rankings.Where(x => x.Retained && x.Available).Take(3).ToArray();
        if (retained.Length == 0) return [];
        var raw = retained.Select(x => Math.Max(.001, x.BestValidationAccuracy - x.BestFar)).ToArray();
        var sum = raw.Sum();
        return retained.Select((x, i) => new ModelEnsembleMember(x.ModelVersion, x.AlgorithmId,
            Math.Round(raw[i] / sum, 4), i == 0 ? "primary" : "corroborator")).ToArray();
    }

    public static ModelEnsembleDecision Decide(IReadOnlyList<ModelEnsembleScore> scores,
        IReadOnlyList<ModelEnsembleMember> members)
    {
        if (scores.Count == 0 || members.Count == 0)
            return new("unknown", null, 0, 0, 0, "没有可用模型结果");
        var weights = members.ToDictionary(x => x.ModelVersion, x => x.Weight, StringComparer.OrdinalIgnoreCase);
        var candidates = scores.GroupBy(x => x.PersonId).Select(group =>
        {
            var weighted = group.Sum(x => (weights.TryGetValue(x.ModelVersion, out var w) ? w : 0) * x.Similarity);
            var votes = group.Count(x => x.Accepted);
            return (PersonId: group.Key, Weighted: weighted, Votes: votes);
        }).OrderByDescending(x => x.Weighted).ToArray();
        var best = candidates.FirstOrDefault();
        var total = scores.Count;
        var threshold = Math.Max(.5, total * .5);
        if (best.PersonId == 0 || best.Weighted < threshold)
            return new("reject", null, best.Weighted, best.Votes, total, "加权分数未达到接受门槛");
        if (best.Votes * 2 < total)
            return new("review", best.PersonId, best.Weighted, best.Votes, total, "模型意见未形成多数一致");
        return new("accept", best.PersonId, best.Weighted, best.Votes, total, "加权分数和模型投票均通过");
    }
}
