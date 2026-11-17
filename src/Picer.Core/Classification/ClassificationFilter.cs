namespace Picer.Core.Classification;

public enum ClassificationMatchMode { All, Any }
public sealed record ClassificationFilterGroup(ClassificationMatchMode Mode, IReadOnlyList<string> IncludedIds);
public sealed record ClassificationFilter(IReadOnlyList<ClassificationFilterGroup> Groups, IReadOnlyList<string> ExcludedIds);

/// <summary>Pure set semantics shared by future SQL/API/UI adapters; inputs must already be scoped.</summary>
public sealed class CompiledClassificationFilter
{
    private readonly (ClassificationMatchMode Mode, IReadOnlySet<string>[] Conditions)[] _groups;
    private readonly HashSet<string> _excluded;
    private readonly ClassificationTaxonomy _taxonomy;

    public CompiledClassificationFilter(ClassificationTaxonomy taxonomy, ClassificationFilter filter)
    {
        ArgumentNullException.ThrowIfNull(taxonomy);
        ArgumentNullException.ThrowIfNull(filter);
        _taxonomy = taxonomy;
        if (filter.Groups is null || filter.ExcludedIds is null || filter.Groups.Count > 8 || filter.ExcludedIds.Count > 256)
            throw new ArgumentException("classification_filter_limit");
        foreach (var group in filter.Groups)
            if (group is null || !Enum.IsDefined(group.Mode) || group.IncludedIds is null || group.IncludedIds.Count > 32)
                throw new ArgumentException("classification_filter_invalid_group");
        _groups = filter.Groups.Where(group => group.IncludedIds.Count > 0).Select(group =>
            (group.Mode, group.IncludedIds.Distinct(StringComparer.Ordinal).Select(Expand).ToArray())).ToArray();
        _excluded = filter.ExcludedIds.Distinct(StringComparer.Ordinal).SelectMany(Expand).ToHashSet(StringComparer.Ordinal);
        IReadOnlySet<string> Expand(string id) => !taxonomy.Labels.ContainsKey(id) && id.StartsWith("person.confirmed.", StringComparison.Ordinal)
            && long.TryParse(id.AsSpan("person.confirmed.".Length), out var personId) && personId > 0
                ? new HashSet<string>(StringComparer.Ordinal) : taxonomy.Expand(id);
    }

    public bool Matches(IEnumerable<string> assignedLabelIds)
    {
        ArgumentNullException.ThrowIfNull(assignedLabelIds);
        var assigned = assignedLabelIds.Where(_taxonomy.Labels.ContainsKey).Select(_taxonomy.CanonicalId).ToHashSet(StringComparer.Ordinal);
        if (assigned.Overlaps(_excluded)) return false;
        return _groups.All(group => group.Mode == ClassificationMatchMode.All
            ? group.Conditions.All(condition => condition.Overlaps(assigned))
            : group.Conditions.Any(condition => condition.Overlaps(assigned)));
    }
}
