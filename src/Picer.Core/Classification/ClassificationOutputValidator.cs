namespace Picer.Core.Classification;

public static class ClassificationOutputValidator
{
    public static void Validate(ClassificationResult result, ClassificationContext context, ClassificationTaxonomySnapshot taxonomy)
    {
        if (result.SchemaVersion != "classification-result-v1" || result.InputFingerprint != ClassificationInputFingerprint.Compute(context.Input)
            || result.Labels is null || result.Proposals is null || result.Proposals.Count > 4)
            throw new InvalidDataException("classification_output_invalid");
        var expected = taxonomy.Labels.Where(l => l.Active && l.Assignable).Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        if (result.Labels.Count != expected.Count || !result.Labels.Select(l => l.LabelId).ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            throw new InvalidDataException("classification_output_incomplete");
        foreach (var label in result.Labels)
            if (label.Support is not ("supported" or "absent" or "uncertain" or "conflicting") || label.EvidenceIds is null
                || label.EvidenceIds.Count > 32 || label.EvidenceIds.Any(id => !context.Evidence.ContainsKey(id))
                || label.Support == "supported" && (label.EvidenceIds.Count == 0 || !label.EvidenceIds.Contains("image:current")))
                throw new InvalidDataException("classification_evidence_invalid");
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
