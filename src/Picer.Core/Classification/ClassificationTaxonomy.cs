using System.Collections.ObjectModel;
using System.Text.Json;

namespace Picer.Core.Classification;

public enum TaxonomyEdgeKind { IsA, Browse, Related }

public sealed record ClassificationLabel(string Id, string Dimension, string Name, string Definition,
    string Exclusions, IReadOnlyList<string> Aliases, bool Assignable = true, bool Active = true, string? RedirectTo = null);
public sealed record ClassificationEdge(string ParentId, string ChildId, TaxonomyEdgeKind Kind);

/// <summary>Immutable, already-authorized graph. This is not an authorization boundary.</summary>
public sealed class ClassificationTaxonomy
{
    public const int MaximumDepth = 16;
    private static readonly HashSet<string> SupportedDimensions = new(StringComparer.Ordinal)
        { "subject", "nature", "place", "architecture", "activity", "event", "object", "environment", "document" };
    private readonly IReadOnlyDictionary<string, ClassificationLabel> _labels;
    private readonly Dictionary<string, string[]> _children;
    public IReadOnlyDictionary<string, ClassificationLabel> Labels => _labels;
    public IReadOnlyList<ClassificationEdge> Edges { get; }

    public ClassificationTaxonomy(IEnumerable<ClassificationLabel> labels, IEnumerable<ClassificationEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(edges);
        var map = new Dictionary<string, ClassificationLabel>(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            ArgumentNullException.ThrowIfNull(label);
            if (!ValidId(label.Id) || !SupportedDimensions.Contains(label.Dimension) || string.IsNullOrWhiteSpace(label.Name)
                || string.IsNullOrWhiteSpace(label.Definition) || label.Aliases is null
                || label.Assignable && string.IsNullOrWhiteSpace(label.Exclusions)
                || label.Aliases.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("taxonomy_invalid_label");
            var copy = label with { Aliases = Array.AsReadOnly(label.Aliases.Distinct(StringComparer.Ordinal).ToArray()) };
            if (!map.TryAdd(label.Id, copy)) throw new ArgumentException("taxonomy_duplicate_label");
            if (map.Count > 10000) throw new ArgumentException("taxonomy_too_large");
        }
        string Canonical(string id)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (map[id].RedirectTo is { } target)
            {
                if (!seen.Add(id) || !map.ContainsKey(target) || map[id].Active || !map[id].Assignable
                    || !map[target].Assignable || map[id].Dimension != map[target].Dimension)
                    throw new ArgumentException("taxonomy_invalid_redirect");
                id = target;
            }
            return id;
        }
        foreach (var id in map.Keys) _ = Canonical(id);
        var edgeSet = new HashSet<ClassificationEdge>();
        foreach (var edge in edges)
        {
            ArgumentNullException.ThrowIfNull(edge);
            if (!map.ContainsKey(edge.ParentId) || !map.ContainsKey(edge.ChildId)
                || edge.ParentId == edge.ChildId || !Enum.IsDefined(edge.Kind))
                throw new ArgumentException("taxonomy_invalid_edge");
            if (!edgeSet.Add(edge)) throw new ArgumentException("taxonomy_duplicate_edge");
            if (edgeSet.Count > 50000) throw new ArgumentException("taxonomy_too_many_edges");
        }
        // Validate the union of semantic and navigation edges, including inactive nodes.
        var adjacency = map.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        var degree = map.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        foreach (var edge in edgeSet.Where(edge => edge.Kind != TaxonomyEdgeKind.Related))
        {
            var parent = Canonical(edge.ParentId); var child = Canonical(edge.ChildId);
            if (parent == child) throw new ArgumentException("taxonomy_redirect_cycle");
            adjacency[parent].Add(child);
            degree[child]++;
        }
        var queue = new Queue<string>(degree.Where(item => item.Value == 0).Select(item => item.Key));
        var depths = map.Keys.ToDictionary(id => id, _ => 1, StringComparer.Ordinal);
        var visited = 0;
        while (queue.TryDequeue(out var id))
        {
            visited++;
            foreach (var child in adjacency[id])
            {
                depths[child] = Math.Max(depths[child], depths[id] + 1);
                if (depths[child] > MaximumDepth) throw new ArgumentException("taxonomy_depth_limit");
                if (--degree[child] == 0) queue.Enqueue(child);
            }
        }
        if (visited != map.Count) throw new ArgumentException("taxonomy_cycle");
        _labels = new ReadOnlyDictionary<string, ClassificationLabel>(map);
        _children = adjacency.ToDictionary(pair => pair.Key,
            pair => pair.Value.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        Edges = Array.AsReadOnly(edgeSet.OrderBy(edge => edge.ParentId, StringComparer.Ordinal)
            .ThenBy(edge => edge.ChildId, StringComparer.Ordinal).ThenBy(edge => edge.Kind).ToArray());
    }

    public IReadOnlySet<string> Expand(string labelId)
    {
        labelId = CanonicalId(labelId);
        if (!_labels.TryGetValue(labelId, out var root) || !root.Active)
            throw new ArgumentException("taxonomy_label_unavailable", nameof(labelId));
        var found = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(labelId);
        while (stack.TryPop(out var id))
        {
            if (!_labels[id].Active || !found.Add(id)) continue;
            foreach (var child in _children[id]) stack.Push(child);
        }
        return found;
    }

    public string CanonicalId(string labelId)
    {
        if (!_labels.ContainsKey(labelId)) throw new ArgumentException("taxonomy_label_unavailable");
        while (_labels[labelId].RedirectTo is { } target) labelId = target;
        return labelId;
    }

    // Ambiguous aliases deliberately return multiple candidates; never auto-merge.
    public IReadOnlyList<string> ResolveAlias(string text, string? dimension = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var normalized = text.Trim().Normalize();
        return _labels.Values.Where(label => label.Active && (dimension is null || label.Dimension == dimension)
            && label.Aliases.Append(label.Name).Any(alias => string.Equals(alias.Normalize(), normalized, StringComparison.OrdinalIgnoreCase)))
            .Select(label => label.Id).Order(StringComparer.Ordinal).ToArray();
    }

    private static bool ValidId(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 128
        && value[0] is >= 'a' and <= 'z'
        && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');
}

public sealed record ClassificationPreset(string Version, IReadOnlyList<ClassificationLabel> Labels,
    IReadOnlyList<ClassificationEdge> Edges)
{
    public ClassificationTaxonomy CreateTaxonomy() => new(Labels, Edges);

    /// <summary>
    /// Adds newly shipped default labels to an existing user taxonomy without overwriting
    /// user definitions, deactivations, redirects, or customized navigation. Missing
    /// definitions are added too, so incremental classification re-evaluates old photos.
    /// </summary>
    public ClassificationTaxonomySnapshot Upgrade(ClassificationTaxonomySnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var labels = current.Labels.ToDictionary(label => label.Id, StringComparer.Ordinal);
        var additions = Labels.Where(label => !labels.ContainsKey(label.Id)).ToArray();
        foreach (var label in additions) labels.Add(label.Id, label);

        var addedIds = additions.Select(label => label.Id).ToHashSet(StringComparer.Ordinal);
        var edges = current.Edges.ToHashSet();
        foreach (var edge in Edges.Where(edge => addedIds.Contains(edge.ParentId) || addedIds.Contains(edge.ChildId)))
            if (labels.ContainsKey(edge.ParentId) && labels.ContainsKey(edge.ChildId)) edges.Add(edge);

        var definitions = current.Definitions.ToDictionary(value => value.LabelId, StringComparer.Ordinal);
        var missingDefinitions = labels.Values.Where(label => label.Assignable && !definitions.ContainsKey(label.Id))
            .Select(label => new ClassificationDefinitionVersion(label.Id, 1)).ToArray();
        if (additions.Length == 0 && missingDefinitions.Length == 0) return current;
        foreach (var definition in missingDefinitions) definitions.Add(definition.LabelId, definition);

        var upgraded = current with
        {
            Revision = checked(current.Revision + 1),
            DiscoveryRevision = checked(current.DiscoveryRevision + 1),
            Labels = labels.Values.OrderBy(label => label.Id, StringComparer.Ordinal).ToArray(),
            Edges = edges.OrderBy(edge => edge.ParentId, StringComparer.Ordinal).ThenBy(edge => edge.ChildId, StringComparer.Ordinal)
                .ThenBy(edge => edge.Kind).ToArray(),
            Definitions = definitions.Values.OrderBy(value => value.LabelId, StringComparer.Ordinal).ToArray()
        };
        _ = new ClassificationTaxonomy(upgraded.Labels, upgraded.Edges);
        return upgraded;
    }

    public static ClassificationPreset Load()
    {
        using var stream = typeof(ClassificationPreset).Assembly.GetManifestResourceStream("Picer.Classification.presets-v1.json")
            ?? throw new InvalidOperationException("classification_preset_missing");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var preset = JsonSerializer.Deserialize<ClassificationPreset>(stream, options)
            ?? throw new InvalidDataException("classification_preset_invalid");
        if (preset.Version != "family-taxonomy-v2") throw new InvalidDataException("classification_preset_version");
        _ = preset.CreateTaxonomy();
        return preset;
    }
}
