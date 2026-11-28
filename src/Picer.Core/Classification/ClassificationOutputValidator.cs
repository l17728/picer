namespace Picer.Core.Classification;

public static class ClassificationOutputValidator
{
    public static void Validate(ClassificationResult result, ClassificationContext context, ClassificationTaxonomySnapshot taxonomy)
    {
        if (result.SchemaVersion != "classification-result-v1" || result.InputFingerprint != ClassificationInputFingerprint.Compute(context.Input)
            || result.Labels is null || result.Proposals is null || result.Proposals.Count > 4)
        {
            Picer.Core.Logging.AppLogger.Warn($"Classification result contract; schemaMatches={result.SchemaVersion == "classification-result-v1"}; fingerprintMatches={result.InputFingerprint == ClassificationInputFingerprint.Compute(context.Input)}; labels={result.Labels?.Count}; proposals={result.Proposals?.Count}");
            throw new InvalidDataException("classification_output_invalid");
        }
        var expected = taxonomy.Labels.Where(l => l.Active && l.Assignable).Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        if (result.Labels.Count != expected.Count || !result.Labels.Select(l => l.LabelId).ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            throw new InvalidDataException("classification_output_incomplete");
        foreach (var label in result.Labels)
            if (label.Support is not ("supported" or "absent" or "uncertain" or "conflicting") || label.EvidenceIds is null
                || label.EvidenceIds.Count > 32 || label.EvidenceIds.Any(id => !context.Evidence.ContainsKey(id))
                || label.Support == "supported" && (label.EvidenceIds.Count == 0 || !label.EvidenceIds.Contains("image:current")))
                throw new InvalidDataException("classification_evidence_invalid");
        // Near-total coverage of a large household taxonomy reproduced indiscriminate
        // model output. Request a fresh judgment before publishing any of its labels.
        var supportedCount = result.Labels.Count(label => label.Support == "supported");
        if (expected.Count >= 100 && supportedCount * 10L >= expected.Count * 9L)
        {
            Picer.Core.Logging.AppLogger.Warn($"Classification saturated response; supported={supportedCount}; assignable={expected.Count}");
            throw new InvalidDataException("classification_output_incomplete");
        }
        foreach (var draft in result.Proposals)
        {
            if (string.IsNullOrWhiteSpace(draft.Name) || draft.Name.Length > 80 || string.IsNullOrWhiteSpace(draft.Definition)
                || draft.Definition.Length > 1500 || string.IsNullOrWhiteSpace(draft.Exclusions) || draft.Exclusions.Length > 1000
                || draft.ParentIds is null || draft.ParentIds.Count is < 1 or > 3 || draft.ParentIds.Any(id => !taxonomy.Labels.Any(l => l.Id == id && l.Active))
                || draft.EvidenceIds is null || !draft.EvidenceIds.Contains("image:current") || draft.EvidenceIds.Any(id => !context.Evidence.ContainsKey(id)))
                throw new InvalidDataException("classification_proposal_invalid");
            _ = new ClassificationTaxonomy([new("dynamic.validation", draft.Dimension, draft.Name, draft.Definition, draft.Exclusions, [])], []);
        }
    }
}
