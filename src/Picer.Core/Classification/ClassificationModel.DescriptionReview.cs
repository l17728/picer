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
        if (matches.Count > 0)
            foreach (var label in active.Where(label => label.Id is "subject.people" or "subject.group"))
                matches.Add(label.Id);
        var parents = taxonomy.Edges.Where(edge => edge.Kind == TaxonomyEdgeKind.IsA).ToLookup(edge => edge.ChildId, edge => edge.ParentId);
        var queue = new Queue<string>(matches);
        while (queue.TryDequeue(out var child))
            foreach (var parent in parents[child]) if (matches.Add(parent)) queue.Enqueue(parent);
        return active.Where(label => matches.Contains(label.Id)).ToArray();
    }

    private const int MaximumReviewExtras = 12;

    private static string ReviewInstruction(bool distinguishDepictions) => $$"""
        重新看当前图片。以下是待核对的人物与描述候选，请逐项判断真实画面里是否看得到。描述、纸面文字或插图提到一个概念不足以确认。
        返回 classification-reviewed-v2。checks 是以每个候选 ID 为键、{{(distinguishDepictions ? "visible、depiction_only 或 absent" : "true 或 false")}} 为值的对象。全部不符时可以全部为 {{(distinguishDepictions ? "absent" : "false")}}。
        另有明确可见的主题可在 additionalSubjects 自由写简短中文名称，没有则填空数组。季节和天气需要画面中有独特可见的证据。保留 inputFingerprint、evaluatedAll 和 proposals。
        候选 [ID,名称,定义,排除条件,维度]：
        """;

    private static object ProposalFormat()
    {
        var text = new { type = "string" };
        var strings = new { type = "array", items = text };
        return new { type = "object", properties = new { name = text, dimension = text, definition = text, exclusions = text, parentIds = strings, evidenceIds = strings },
            required = new[] { "name", "dimension", "definition", "exclusions", "parentIds", "evidenceIds" }, additionalProperties = false };
    }

    private static object ReviewFormat(ClassificationLabel[] candidates, string fingerprint, ClassificationTaxonomySnapshot? emptyReview, bool distinguishDepictions)
    {
        var ids = candidates.Select(label => label.Id).ToArray();
        var extras = new { type = "array", items = new { type = "string", minLength = 1, maxLength = 40 }, maxItems = MaximumReviewExtras };
        var properties = new Dictionary<string, object> { ["observation"] = new { type = "string", maxLength = 160 } };
        if (emptyReview is not null)
        {
            var parents = emptyReview.Labels.Where(label => label.Active && !label.Assignable).Select(label => label.Id).ToArray();
            properties["primarySubject"] = new { type = "object", properties = new {
                name = new { type = "string", minLength = 1, maxLength = 32 },
                visible = new { type = "string", minLength = 1, maxLength = 160 },
                boundary = new { type = "string", minLength = 1, maxLength = 96 },
                parentId = new { type = "string", @enum = parents.Append("").ToArray() },
                kind = new { type = "string", @enum = new[] { "visible_subject", "none" } } },
                required = new[] { "name", "visible", "boundary", "parentId", "kind" }, additionalProperties = false };
        }
        properties["schemaVersion"] = new { type = "string", @enum = new[] { "classification-reviewed-v2" } };
        properties["inputFingerprint"] = new { type = "string", @enum = new[] { fingerprint } };
        properties["evaluatedAll"] = new { type = "boolean" };
        object checkStatus = !distinguishDepictions ? new { type = "boolean" } :
            new { type = "string", @enum = new[] { "visible", "depiction_only", "absent" } };
        properties["checks"] = new { type = "object", properties = ids.ToDictionary(id => id, _ => checkStatus), required = ids, additionalProperties = false };
        properties["additionalSubjects"] = extras;
        properties["proposals"] = new { type = "array", items = ProposalFormat(), maxItems = 4 };
        var schema = new { type = "object", properties, required = properties.Keys, additionalProperties = false };
        return new { type = "json_schema", json_schema = new { name = "review_visible_photo_topics", schema } };
    }

    internal static ClassificationResult DecodeReview(string content, ClassificationTaxonomySnapshot taxonomy, ClassificationLabel[] candidates, bool emptyReview = false, bool distinguishDepictions = false)
    {
        using var document = JsonDocument.Parse(ExtractJsonObject(content));
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "classification-reviewed-v2"
            || !root.TryGetProperty("evaluatedAll", out var complete) || complete.ValueKind != JsonValueKind.True
            || !root.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("additionalSubjects", out var extras) || extras.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("inputFingerprint", out var fingerprint) || fingerprint.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("proposals", out var proposals) || proposals.ValueKind != JsonValueKind.Array)
            throw ReviewIncomplete("envelope");
        var pending = candidates.Select(label => label.Id).ToHashSet(StringComparer.Ordinal);
        var active = taxonomy.Labels.Where(label => label.Active && label.Assignable).Select(label => label.Id).ToArray();
        var names = taxonomy.Labels.Where(label => label.Active && label.Assignable)
            .SelectMany(label => label.Aliases.Append(label.Name).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => (Name: name, label.Id))).ToLookup(pair => pair.Name, pair => pair.Id, StringComparer.OrdinalIgnoreCase);
        var positive = new HashSet<string>(StringComparer.Ordinal);
        foreach (var check in checks.EnumerateObject())
        {
            if (!pending.Remove(check.Name)) throw ReviewIncomplete("invalid_or_duplicate_check");
            if (distinguishDepictions)
            {
                if (check.Value.ValueKind != JsonValueKind.String || check.Value.GetString() is not ("visible" or "depiction_only" or "absent"))
                    throw ReviewIncomplete("invalid_subject_check");
                if (check.Value.GetString() == "visible") positive.Add(check.Name);
            }
            else
            {
                if (check.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw ReviewIncomplete("invalid_check");
                if (check.Value.GetBoolean()) positive.Add(check.Name);
            }
        }
        if (pending.Count != 0) throw ReviewIncomplete("missing_check");
        if (extras.GetArrayLength() > MaximumReviewExtras) throw ReviewIncomplete("too_many_extras");
        foreach (var extra in extras.EnumerateArray())
        {
            if (extra.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(extra.GetString()) || extra.GetString()!.Length > 40)
                throw ReviewIncomplete("invalid_extra");
            var matches = names[extra.GetString()!.Trim()].ToArray();
            if (matches.Length == 1 && !checks.TryGetProperty(matches[0], out _)) positive.Add(matches[0]);
        }
        var drafts = proposals.Deserialize<ClassificationProposalDraft[]>(ClassificationRepository.Json)
            ?? throw ReviewIncomplete("invalid_proposals");
        if (emptyReview)
        {
            if (!root.TryGetProperty("primarySubject", out var subject) || subject.ValueKind != JsonValueKind.Object
                || !subject.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String
                || kind.GetString() is not ("none" or "visible_subject"))
                throw ReviewIncomplete("missing_primary_subject");
            var term = kind.GetString() == "none" ? "" :
                subject.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString())
                    ? name.GetString()!.Trim() : throw ReviewIncomplete("missing_primary_subject_name");
            var matches = names[term].ToArray();
            if (matches.Length == 1 && !checks.TryGetProperty(matches[0], out _)) positive.Add(matches[0]);
            if (term.Length is > 0 and <= 32 && drafts.Length == 0 && matches.Length == 0
                && !taxonomy.Labels.Any(label => label.Active && !label.Assignable && label.Aliases.Append(label.Name).Contains(term, StringComparer.OrdinalIgnoreCase))
                && subject.TryGetProperty("parentId", out var parentId) && parentId.ValueKind == JsonValueKind.String
                && taxonomy.Labels.SingleOrDefault(label => label.Active && !label.Assignable && label.Id == parentId.GetString()) is { } parent
                && subject.TryGetProperty("visible", out var visible) && visible.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(visible.GetString())
                && subject.TryGetProperty("boundary", out var boundary) && boundary.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(boundary.GetString()))
                drafts = [new(term, parent.Dimension, visible.GetString()!, boundary.GetString()!, [parent.Id], ["image:current"])];
            if (positive.Contains("document.screen_photo")) positive.Remove("document.screenshot");
        }
        return new("classification-result-v1", fingerprint.GetString()!, active.Select(id => positive.Contains(id)
            ? new ClassificationLabelEvaluation(id, "supported", ["image:current"]) : new ClassificationLabelEvaluation(id, "absent", [])).ToArray(),
            drafts);
    }
    private static InvalidDataException ReviewIncomplete(string reason)
    {
        Picer.Core.Logging.AppLogger.Warn($"Classification description review rejected; reason={reason}");
        return new InvalidDataException("classification_output_incomplete");
    }

}
