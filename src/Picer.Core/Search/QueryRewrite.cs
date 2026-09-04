namespace Picer.Core.Search;

/// <summary>Small, deterministic synonym layer used before the LLM review stage.</summary>
/// <remarks>The LLM may add terms, but this bounded vocabulary prevents untrusted SQL
/// and gives stable recall for common Chinese natural-language variants.</remarks>
public static class QueryRewrite
{
    private static readonly IReadOnlyDictionary<string, string[]> Synonyms =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["白天"] = ["白日", "白昼", "上午", "中午", "下午"],
            ["夜间"] = ["晚上", "夜晚", "深夜"],
            ["爬山"] = ["登山", "徒步", "山地徒步"],
            ["登山"] = ["爬山", "徒步", "山地徒步"],
            ["红色"] = ["红", "红裙", "红色裙子"],
            ["上海"] = ["申城", "上海市"],
            ["北京"] = ["北京市", "京城"]
        };

    public static IReadOnlyList<string> Expand(IEnumerable<string>? terms, bool includeTimeTerms = false)
    {
        var result = new List<string>();
        foreach (var raw in terms ?? [])
        {
            var term = raw?.Trim();
            if (string.IsNullOrWhiteSpace(term)) continue;
            if (!includeTimeTerms && (term.Contains("白天") || term.Contains("白日") || term.Contains("白昼") ||
                term.Contains("晚上") || term.Contains("夜间") || term.Contains("夜晚") || term.Contains("深夜"))) continue;
            Add(result, term);
            foreach (var pair in Synonyms)
                if (term.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
                    foreach (var synonym in pair.Value) Add(result, synonym);
        }
        return result;
    }

    private static void Add(List<string> values, string value)
    {
        if (!values.Contains(value, StringComparer.OrdinalIgnoreCase)) values.Add(value);
    }
}
