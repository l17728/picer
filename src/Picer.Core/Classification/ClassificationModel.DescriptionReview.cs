using System.Text.Json;

namespace Picer.Core.Classification;

public sealed partial class ClassificationModel
{
    // An index description can suggest questions, never answers. Only the model
    // reviewing the current pixels can turn a candidate into a machine suggestion.
    internal static ClassificationLabel[] DescriptionCandidates(ClassificationContext context, ClassificationTaxonomySnapshot taxonomy)
    {
        var terms = new List<string>();
        foreach (var entry in context.Evidence.Where(e => e.Key.StartsWith("description:", StringComparison.Ordinal)))
        {
            try
            {
                using var document = JsonDocument.Parse(entry.Value);
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var field in document.RootElement.EnumerateObject())
                {
                    if (field.Name.ToLowerInvariant() is not ("summary" or "objects" or "activities" or "scenes" or "searchterms")) continue;
                    if (field.Value.ValueKind == JsonValueKind.String) terms.Add(field.Value.GetString()!);
                    else if (field.Value.ValueKind == JsonValueKind.Array)
                        terms.AddRange(field.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!));
                }
            }
            catch (JsonException) { /* A legacy description may be plain text. */ }
        }
        if (terms.Count == 0) return [];
        var active = taxonomy.Labels.Where(label => label.Active && label.Assignable).ToArray();
        var matches = active.Where(label => label.Aliases.Append(label.Name).Any(name => name.Length > 1
            && terms.Any(term => term.Contains(name, StringComparison.OrdinalIgnoreCase)))).Select(label => label.Id).ToHashSet(StringComparer.Ordinal);
        var parents = taxonomy.Edges.Where(edge => edge.Kind == TaxonomyEdgeKind.IsA).ToLookup(edge => edge.ChildId, edge => edge.ParentId);
        var queue = new Queue<string>(matches);
        while (queue.TryDequeue(out var child))
            foreach (var parent in parents[child]) if (matches.Add(parent)) queue.Enqueue(parent);
        return active.Where(label => matches.Contains(label.Id)).ToArray();
    }

    private const int MaximumReviewExtras = 12;

    private const string ReviewInstruction = """
        重新看当前图片。以下是索引描述提及的候选，请逐项判断真实画面里是否看得到。描述、纸面文字或插图提到一个概念不足以确认。
        返回 classification-reviewed-v1。checks 是以每个候选 ID 为键、true 或 false 为值的对象。全部不符时可以全部为 false。
        另有明确可见的词表标签可写入 additionalIds；不要重复 checks。保留 inputFingerprint、evaluatedAll 和 proposals。
        候选 [ID,名称,定义,排除条件]：
        """;

    private static object ProposalFormat()
    {
        var text = new { type = "string" };
        var strings = new { type = "array", items = text };
        return new { type = "object", properties = new { name = text, dimension = text, definition = text, exclusions = text, parentIds = strings, evidenceIds = strings },
            required = new[] { "name", "dimension", "definition", "exclusions", "parentIds", "evidenceIds" }, additionalProperties = false };
    }

    private static object ReviewFormat(ClassificationLabel[] candidates, ClassificationLabel[] active, string fingerprint)
    {
        var ids = candidates.Select(label => label.Id).ToArray();
        var remaining = active.Select(label => label.Id).Except(ids, StringComparer.Ordinal).ToArray();
        object extras = remaining.Length == 0 ? new { type = "array", maxItems = 0 } :
            new { type = "array", items = new { type = "string", @enum = remaining }, maxItems = Math.Min(remaining.Length, MaximumReviewExtras) };
        var schema = new { type = "object", properties = new {
            observation = new { type = "string", maxLength = 160 },
            schemaVersion = new { type = "string", @enum = new[] { "classification-reviewed-v1" } },
            inputFingerprint = new { type = "string", @enum = new[] { fingerprint } },
            evaluatedAll = new { type = "boolean" },
            checks = new { type = "object", properties = ids.ToDictionary(id => id, _ => new { type = "boolean" }), required = ids, additionalProperties = false },
            additionalIds = extras, proposals = new { type = "array", items = ProposalFormat(), maxItems = 4 } },
            required = new[] { "observation", "schemaVersion", "inputFingerprint", "evaluatedAll", "checks", "additionalIds", "proposals" }, additionalProperties = false };
        return new { type = "json_schema", json_schema = new { name = "review_visible_photo_topics", schema } };
    }

    internal static ClassificationResult DecodeReview(string content, ClassificationTaxonomySnapshot taxonomy, ClassificationLabel[] candidates)
    {
        using var document = JsonDocument.Parse(ExtractJsonObject(content));
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "classification-reviewed-v1"
            || !root.TryGetProperty("evaluatedAll", out var complete) || complete.ValueKind != JsonValueKind.True
            || !root.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("additionalIds", out var extras) || extras.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("inputFingerprint", out var fingerprint) || fingerprint.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("proposals", out var proposals) || proposals.ValueKind != JsonValueKind.Array)
            throw ReviewIncomplete("envelope");
        var pending = candidates.Select(label => label.Id).ToHashSet(StringComparer.Ordinal);
        var active = taxonomy.Labels.Where(label => label.Active && label.Assignable).Select(label => label.Id).ToArray();
        var other = active.Except(pending).ToHashSet(StringComparer.Ordinal);
        var positive = new HashSet<string>(StringComparer.Ordinal);
        foreach (var check in checks.EnumerateObject())
        {
            if (!pending.Remove(check.Name) || check.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw ReviewIncomplete("invalid_or_duplicate_check");
            if (check.Value.GetBoolean()) positive.Add(check.Name);
        }
        if (pending.Count != 0) throw ReviewIncomplete("missing_check");
        if (extras.GetArrayLength() > MaximumReviewExtras) throw ReviewIncomplete("too_many_extras");
        foreach (var extra in extras.EnumerateArray())
        {
            if (extra.ValueKind != JsonValueKind.String || !other.Contains(extra.GetString()!)) throw ReviewIncomplete("invalid_extra");
            positive.Add(extra.GetString()!);
        }
        return new("classification-result-v1", fingerprint.GetString()!, active.Select(id => positive.Contains(id)
            ? new ClassificationLabelEvaluation(id, "supported", ["image:current"]) : new ClassificationLabelEvaluation(id, "absent", [])).ToArray(),
            proposals.Deserialize<ClassificationProposalDraft[]>(ClassificationRepository.Json)!);
    }
    private static InvalidDataException ReviewIncomplete(string reason)
    {
        Picer.Core.Logging.AppLogger.Warn($"Classification description review rejected; reason={reason}");
        return new InvalidDataException("classification_output_incomplete");
    }

}
