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
    public static IReadOnlyDictionary<string, string> DisplayNames => Terms.ToDictionary(pair => pair.Key, pair => pair.Value[0]);
    private static readonly IReadOnlyDictionary<string, string[]> Terms = new Dictionary<string, string[]>
    {
        ["metadata.orientation.landscape"] = ["横向照片"],
        ["metadata.orientation.portrait"] = ["纵向照片"],
        ["metadata.orientation.square"] = ["方形照片"],
        ["metadata.camera.present"] = ["包含相机信息"],
        ["metadata.gps.present"] = ["包含 GPS 坐标"],
        ["season.summer"] = ["夏天", "夏季"],
        ["apparel.dress"] = ["连衣裙", "裙子", "长裙", "短裙"],
        ["scene.mountain"] = ["山上", "山里", "山地", "山区", "山景"],
        ["scene.beach"] = ["海边", "沙滩", "海滩"],
        ["scene.snow"] = ["雪地", "下雪", "雪景"],
        ["activity.hiking"] = ["徒步", "爬山", "登山"],
        ["activity.dining"] = ["聚餐", "吃饭", "餐桌"]
    };

    public static bool TryResolve(string? value, out string conceptId)
    {
        var normalized = value?.Trim();
        if (!string.IsNullOrEmpty(normalized))
        {
            foreach (var pair in Terms)
            {
                if (pair.Key.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                    pair.Value.Any(term => term.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    conceptId = pair.Key;
                    return true;
                }
            }
        }
        conceptId = "";
        return false;
    }

    public static string DisplayName(string conceptId) =>
        DisplayNames.TryGetValue(conceptId, out var value) ? value : conceptId;

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
