using Picer.Core.Classification;
using Picer.Core.Data;

namespace Picer.Core.Search;

public sealed record ClassificationEvidence(string Id, string Name, string Source, string Snapshot, long TaxonomyRevision);

/// <summary>A separate recall projection; never replaces description FTS or confirmed people constraints.</summary>
public sealed class ClassificationEvidenceIndex(ClassificationReplica replica, bool includeSuggestions)
{
    private readonly ClassificationTaxonomy _graph = new(replica.Taxonomy.Labels, replica.Taxonomy.Edges);
    public IReadOnlyList<string> ExplicitTerms(string question)
    {
        var matches = new List<(string Name, int Start, int Length)>();
        foreach (var label in _graph.Labels.Values.Where(label => label.Active && !label.Id.StartsWith("person.confirmed.", StringComparison.Ordinal)))
        foreach (var name in label.Aliases.Prepend(label.Name).Where(name => name.Length >= 2))
        {
            var start = question.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            if (start < 0) continue;
            var prefix = question[..start];
            if (System.Text.RegularExpressions.Regex.IsMatch(prefix, "(?:不要|不含|排除|没有|不是|非)\\s*$")) continue;
            if (name.All(char.IsAsciiLetter) && (start > 0 && char.IsAsciiLetter(question[start - 1])
                || start + name.Length < question.Length && char.IsAsciiLetter(question[start + name.Length]))) continue;
            matches.Add((name, start, name.Length));
        }
        return matches.Where(value => !matches.Any(other => other.Start <= value.Start
            && other.Start + other.Length >= value.Start + value.Length && other.Length > value.Length))
            .Select(value => value.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyDictionary<long, IReadOnlyList<ClassificationEvidence>> Match(PicerDatabase database, IReadOnlyList<string> terms,
        bool combineTextEvidence = false)
    {
        if (terms.Count == 0) return new Dictionary<long, IReadOnlyList<ClassificationEvidence>>();
        var groups = terms.Select(term => _graph.Labels.Values
            .Where(label => label.Active && !label.Id.StartsWith("person.confirmed.", StringComparison.Ordinal)
                && label.Aliases.Prepend(label.Name).Any(name => name.Equals(term, StringComparison.OrdinalIgnoreCase)
                    || name.Length >= 2 && term.Contains(name, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(label => _graph.Expand(label.Id)).ToHashSet(StringComparer.Ordinal)).ToArray();
        if (!combineTextEvidence && groups.Any(group => group.Count == 0)) return new Dictionary<long, IReadOnlyList<ClassificationEvidence>>();
        var textGroups = combineTextEvidence ? terms.Select(term => new MediaSearchTool(database)
            .ExecuteAll(new(Terms: QueryRewrite.Expand([term]), AutoRelax: false))
            .Items.Select(hit => hit.Id).ToHashSet()).ToArray() : [];
        var result = new Dictionary<long, IReadOnlyList<ClassificationEvidence>>();
        foreach (var photo in replica.Photos)
        {
            var confirmed = (photo.ConfirmedIds ?? []).ToHashSet(StringComparer.Ordinal);
            var evidence = (photo.EvidenceLabelIds ?? []).ToHashSet(StringComparer.Ordinal);
            var legacy = includeSuggestions ? (photo.LegacyIds ?? []).ToHashSet(StringComparer.Ordinal) : [];
            var description = includeSuggestions ? (photo.LegacySuggestedIds ?? []).ToHashSet(StringComparer.Ordinal) : [];
            var ids = confirmed.Concat(evidence).Concat(legacy).Concat(description)
                .Concat(includeSuggestions && photo.State == "succeeded" ? photo.SuggestedIds ?? [] : [])
                .Except(photo.ConflictLabelIds ?? []).Where(id => _graph.Labels.TryGetValue(id, out var label) && label.Active && label.Assignable)
                .ToHashSet(StringComparer.Ordinal);
            if (!groups.Select((group, index) => group.Overlaps(ids) || combineTextEvidence && textGroups[index].Contains(photo.ImageId)).All(value => value)) continue;
            if (!groups.Any(group => group.Overlaps(ids))) continue;
            if (database.GetImage(photo.ImageId) is not { Status: Picer.Core.Models.ImageStatus.Active }) continue;
            if (database.GetPhotoClassificationContentIdentity(photo.ImageId) != photo.ContentIdentity) continue;
            result[photo.ImageId] = ids.Where(id => groups.Any(group => group.Contains(id)))
                .Select(id => new ClassificationEvidence(id, _graph.Labels[id].Name,
                    confirmed.Contains(id) ? "人工确认" : evidence.Contains(id) ? "人物证据"
                        : description.Contains(id) ? "描述派生建议" : legacy.Contains(id) ? "历史模型分类" : "模型建议",
                    replica.Snapshot, replica.Taxonomy.Revision)).ToArray();
        }
        return result;
    }
}
