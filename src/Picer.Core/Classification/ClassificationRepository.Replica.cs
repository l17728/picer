namespace Picer.Core.Classification;

public sealed partial class ClassificationRepository
{
    // A separate capability channel: legacy metadata snapshots cannot erase or reinterpret these rows.
    public void SaveReplica(string origin, ClassificationReplica replica)
    {
        if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(replica.Epoch) || replica.Photos.Count > 1000000
            || replica.Photos.Select(p => p.ImageId).Distinct().Count() != replica.Photos.Count)
            throw new InvalidDataException("classification_replica_invalid");
        _ = new ClassificationTaxonomy(replica.Taxonomy.Labels, replica.Taxonomy.Edges);
        Execute("""
            INSERT INTO classification_v2_mirror VALUES($origin,$epoch,$revision,$payload)
            ON CONFLICT(origin) DO UPDATE SET epoch=excluded.epoch,revision=excluded.revision,payload=excluded.payload
            WHERE classification_v2_mirror.revision<>excluded.revision OR classification_v2_mirror.epoch<>excluded.epoch
            """, ("$origin", origin), ("$epoch", replica.Epoch), ("$revision", replica.Snapshot), ("$payload", Encode(replica)));
    }

    public ClassificationReplica? LoadReplica(string origin)
    {
        var payload = Scalar("SELECT payload FROM classification_v2_mirror WHERE origin=$origin", ("$origin", origin));
        return payload is null ? null : Decode<ClassificationReplica>(payload);
    }

    public bool TrySaveReplica(string origin, ClassificationReplica replica, string? expectedSnapshot) => Write(() =>
    {
        var current = LoadReplica(origin)?.Snapshot;
        if (current != expectedSnapshot && current != replica.Snapshot) return false;
        SaveReplica(origin, replica);
        return true;
    });

    public void QueueEdit(string origin, string route, string operationId, string payload)
    {
        ValidateRequestId(operationId);
        if (route is not ("decisions" or "proposals/review" or "taxonomy/edit" or "publisher-result")) throw new ArgumentException("classification_outbox_route_invalid");
        var previous = Scalar("SELECT payload FROM classification_v2_outbox WHERE operation_id=$id", ("$id", operationId));
        if (previous is not null && previous != payload) throw new InvalidOperationException("classification_operation_conflict");
        if (previous is not null && (Scalar("SELECT origin FROM classification_v2_outbox WHERE operation_id=$id", ("$id", operationId)) != origin
            || Scalar("SELECT route FROM classification_v2_outbox WHERE operation_id=$id", ("$id", operationId)) != route))
            throw new InvalidOperationException("classification_operation_conflict");
        Execute("INSERT OR IGNORE INTO classification_v2_outbox(operation_id,origin,route,payload) VALUES($id,$origin,$route,$payload)",
            ("$id", operationId), ("$origin", origin), ("$route", route), ("$payload", payload));
    }
    public long OutboxHighWater(string origin) => long.Parse(Scalar("SELECT COALESCE(MAX(rowid),0) FROM classification_v2_outbox WHERE origin=$origin", ("$origin", origin))!);
    public IReadOnlyList<ClassificationOutboxItem> Outbox(string origin, long after = 0, long highWater = long.MaxValue)
    {
        using var command = Sql("SELECT operation_id,route,payload,state,rowid FROM classification_v2_outbox WHERE origin=$origin AND state<>'completed' AND rowid>$after AND rowid<=$high ORDER BY rowid LIMIT 200", ("$origin", origin), ("$after", after), ("$high", highWater));
        using var r = command.ExecuteReader(); var result = new List<ClassificationOutboxItem>();
        while (r.Read()) result.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4)));
        return result;
    }
    public IEnumerable<ClassificationOutboxItem> PendingSnapshot(string origin)
    {
        var high = OutboxHighWater(origin);
        long after = 0;
        while (true)
        {
            var page = Outbox(origin, after, high);
            if (page.Count == 0) yield break;
            after = page[^1].Sequence;
            foreach (var item in page) yield return item;
        }
    }
    public string? EditState(string origin, string operationId) => Scalar(
        "SELECT state FROM classification_v2_outbox WHERE origin=$origin AND operation_id=$id", ("$origin", origin), ("$id", operationId));
    public void MarkEdit(string id, string state, string? expectedState = null)
    {
        if (state is not ("acknowledged" or "completed" or "conflict")) throw new ArgumentException("classification_outbox_state_invalid");
        Execute("UPDATE classification_v2_outbox SET state=$state WHERE operation_id=$id AND ($expected IS NULL OR state=$expected)",
            ("$state", state), ("$id", id), ("$expected", expectedState));
    }

    public int CompleteAcknowledgedEdits(string origin, IReadOnlyList<string> operations) => Write(() =>
    {
        var completed = 0;
        foreach (var id in operations)
            completed += Execute("UPDATE classification_v2_outbox SET state='completed' WHERE origin=$origin AND operation_id=$id AND state='acknowledged'",
                ("$origin", origin), ("$id", id));
        return completed;
    });

    public static ClassificationBrowseResult BrowseReplica(ClassificationReplica replica, ClassificationBrowseRequest request, bool cached = true)
    {
        if (request.PageSize is < 1 or > 200) throw new ArgumentException("classification_page_size_invalid");
        var graph = new ClassificationTaxonomy(replica.Taxonomy.Labels, replica.Taxonomy.Edges);
        var filter = new CompiledClassificationFilter(graph, request.Filter);
        var photos = request.IncludeSuggestions && request.IncludeLegacy ? replica.Photos.ToArray() : replica.Photos.Select(photo => photo with { LabelIds = (photo.ConfirmedIds ?? [])
            .Concat(photo.EvidenceLabelIds ?? [])
            .Concat(request.IncludeSuggestions ? photo.SuggestedIds ?? [] : [])
            .Concat(request.IncludeSuggestions && request.IncludeLegacy ? photo.LegacySuggestedIds ?? [] : [])
            .Concat(request.IncludeLegacy ? photo.LegacyIds ?? [] : []).Distinct().Order(StringComparer.Ordinal).ToArray() }).ToArray();
        var snapshot = (cached ? "cached-" : "") + Digest(replica.Snapshot + request.IncludeSuggestions + request.IncludeLegacy);
        if (request.Snapshot is not null && request.Snapshot != snapshot) throw new InvalidOperationException("classification_snapshot_stale");
        long after = 0;
        if (request.Cursor is not null)
        {
            var parts = request.Cursor.Split(':');
            if (parts.Length != 2 || parts[0] != snapshot || !long.TryParse(parts[1], out after)) throw new InvalidOperationException("classification_cursor_stale");
        }
        var counts = CountLabels(graph, photos);
        var matches = photos.Where(p => filter.Matches(p.LabelIds)).OrderBy(p => p.ImageId).ToArray();
        var page = matches.Where(p => p.ImageId > after).Take(request.PageSize + 1).ToArray();
        return new(snapshot, replica.Taxonomy, replica.Coverage, counts, matches.Length, page.Take(request.PageSize).ToArray(),
            page.Length > request.PageSize ? snapshot + ":" + page[request.PageSize - 1].ImageId : null, cached) { Epoch = replica.Epoch };
    }

    private static IReadOnlyDictionary<string, int> CountLabels(ClassificationTaxonomy graph, IReadOnlyList<ClassificationPhoto> photos)
    {
        var counts = graph.Labels.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var ancestors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var parent in graph.Labels.Values.Where(label => label.Active))
            foreach (var child in graph.Expand(parent.Id))
            {
                if (!ancestors.TryGetValue(child, out var parents)) ancestors[child] = parents = [];
                parents.Add(parent.Id);
            }
        var aggregates = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var photo in photos)
        {
            if (photo.LabelIds.Count == 0) continue;
            var counted = new HashSet<string>(StringComparer.Ordinal);
            var dimensions = new HashSet<string>(StringComparer.Ordinal);
            var namedPeople = false;
            foreach (var label in photo.LabelIds)
            {
                if (graph.Labels.TryGetValue(label, out var definition)) dimensions.Add(definition.Dimension);
                namedPeople |= label.StartsWith("person.confirmed.", StringComparison.Ordinal);
                if (ancestors.TryGetValue(label, out var parents)) counted.UnionWith(parents);
            }
            foreach (var parent in counted) counts[parent]++;
            // Deduplicated aggregates for tree group nodes: several labels of one dimension (for
            // example several named people on one photo) must not add up to more than one photo.
            foreach (var dimension in dimensions)
                aggregates[ClassificationCountKeys.Dimension(dimension)] = aggregates.GetValueOrDefault(ClassificationCountKeys.Dimension(dimension)) + 1;
            if (namedPeople)
                aggregates[ClassificationCountKeys.NamedPeople] = aggregates.GetValueOrDefault(ClassificationCountKeys.NamedPeople) + 1;
        }
        foreach (var pair in aggregates) counts[pair.Key] = pair.Value;
        return counts;
    }
}
