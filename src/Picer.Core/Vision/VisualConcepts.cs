using Picer.Core.Models;

namespace Picer.Core.Vision;

public sealed record VisualConceptScore(string ConceptId, float Confidence);
public sealed record SemanticAnalysisResult(IReadOnlyList<VisualConceptScore> Concepts, string? Caption = null);
public sealed record VisualQueryParse(IReadOnlyList<string> ConceptIds, string RemainingQuery);
public sealed record SemanticBatchResult(int Requested, int Analyzed, int Skipped, int Failed);

public interface IImageSemanticAnalyzer
{
    string AnalyzerName { get; }
    string ModelVersion { get; }
    Task<SemanticAnalysisResult> AnalyzeAsync(ImageRecord image, CancellationToken cancellationToken);
}

public static class VisualConceptCatalog
{
    private static readonly IReadOnlyDictionary<string, string[]> Terms = new Dictionary<string, string[]>
    {
        ["season.summer"] = ["夏天", "夏季"],
        ["apparel.dress"] = ["连衣裙", "裙子", "长裙", "短裙"],
        ["scene.mountain"] = ["山上", "山里", "山地", "山区", "山景"],
        ["scene.beach"] = ["海边", "沙滩", "海滩"],
        ["scene.snow"] = ["雪地", "下雪", "雪景"],
        ["activity.hiking"] = ["徒步", "爬山", "登山"],
        ["activity.dining"] = ["聚餐", "吃饭", "餐桌"]
    };

    public static VisualQueryParse Parse(string? query)
    {
        var remaining = query?.Trim() ?? "";
        var concepts = new List<string>();
        foreach (var entry in Terms.SelectMany(pair => pair.Value.Select(term => (Concept: pair.Key, Term: term)))
                     .OrderByDescending(value => value.Term.Length))
        {
            if (!remaining.Contains(entry.Term, StringComparison.OrdinalIgnoreCase)) continue;
            if (!concepts.Contains(entry.Concept, StringComparer.Ordinal)) concepts.Add(entry.Concept);
            remaining = remaining.Replace(entry.Term, "", StringComparison.OrdinalIgnoreCase);
        }
        remaining = remaining
            .Replace("穿着", "", StringComparison.OrdinalIgnoreCase)
            .Replace("穿", "", StringComparison.OrdinalIgnoreCase)
            .Replace("并且", "", StringComparison.OrdinalIgnoreCase)
            .Replace("而且", "", StringComparison.OrdinalIgnoreCase)
            .Replace("在", "", StringComparison.OrdinalIgnoreCase)
            .Trim(' ', '，', ',', '。', '的');
        return new VisualQueryParse(concepts, remaining);
    }
}
