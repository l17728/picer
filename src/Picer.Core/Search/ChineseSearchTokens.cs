using System.Text;
using System.Text.RegularExpressions;

namespace Picer.Core.Search;

/// <summary>Versioned, offline search tokenization: overlapping Han bigrams and Latin words.
/// This is character tokenization, not dictionary-based Chinese word segmentation.</summary>
public static partial class ChineseSearchTokens
{
    public const string Version = "han-bigram-v1";

    public static string Encode(string text) => string.Join(' ', Tokens(text));

    public static string Query(IEnumerable<string> terms, bool requireAll = false)
    {
        var clauses = terms.Select(term => Tokens(term).Distinct(StringComparer.Ordinal).ToArray())
            .Where(tokens => tokens.Length > 0)
            .Select(tokens => "(" + string.Join(" AND ", tokens.Select(token => "\"" + token + "\"")) + ")");
        return string.Join(requireAll ? " AND " : " OR ", clauses);
    }

    private static IEnumerable<string> Tokens(string text)
    {
        foreach (Match match in Runs().Matches(text.Normalize(NormalizationForm.FormKC).ToLowerInvariant()))
        {
            var run = match.Value;
            if (run[0] is >= '\u3400' and <= '\u9fff')
            {
                // Prefixes prevent Latin input colliding with encoded Han characters.
                foreach (var ch in run) yield return "h" + ((int)ch).ToString("x4");
                for (var i = 1; i < run.Length; i++)
                    yield return "b" + ((int)run[i - 1]).ToString("x4") + ((int)run[i]).ToString("x4");
            }
            else yield return "w" + run;
        }
    }

    [GeneratedRegex("[\\u3400-\\u9fff]+|[\\p{L}\\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Runs();
}
