using Picer.Core.Data;

namespace Picer.Core.Classification;

public sealed partial class ClassificationRepository
{
    public int Decide(ClassificationDecision decision) => Write(() =>
    {
        ValidateRequestId(decision.OperationId);
        var hash = Digest(Encode(decision));
        var receipt = Scalar("SELECT payload_hash FROM classification_v2_changes WHERE operation_id=$id", ("$id", decision.OperationId));
        if (receipt is not null)
        {
            if (receipt != hash) throw new InvalidOperationException("classification_operation_conflict");
            return 0;
        }
        if (Epoch != decision.Epoch) throw new InvalidOperationException("classification_epoch_changed");
        var taxonomy = Taxonomy();
        if (decision.ExpectedTaxonomyRevision != taxonomy.Revision || decision.ManualFingerprints is null)
            throw new InvalidOperationException("classification_review_snapshot_stale");
        var manual = ManualFingerprints();
        var graph = new ClassificationTaxonomy(taxonomy.Labels, taxonomy.Edges);
        var canonical = graph.CanonicalId(decision.LabelId);
        if (decision.ContentIdentities.Count is < 1 or > 200 || decision.Decision is not ("confirm" or "reject" or "restore")
            || !graph.Labels[canonical].Active || !graph.Labels[canonical].Assignable)
            throw new ArgumentException("classification_decision_invalid");
        foreach (var image in decision.ContentIdentities)
        {
            var owner = Scalar("SELECT owner_key FROM sync_image_owners WHERE image_id=$id", ("$id", image.Key));
            if (owner is null || owner.StartsWith("ambiguous-source:", StringComparison.Ordinal)
                || !decision.Owners.TryGetValue(image.Key, out var expectedOwner) || owner != expectedOwner)
                throw new InvalidOperationException("classification_owner_changed");
            if (database.GetPhotoClassificationContentIdentity(image.Key, _transaction) != image.Value)
                throw new InvalidOperationException("classification_input_changed");
            if (!decision.ManualFingerprints.TryGetValue(image.Key, out var observed)
                || observed != manual.GetValueOrDefault((owner, image.Value), Digest("")))
                throw new InvalidOperationException("classification_manual_review_changed");
        }
        foreach (var image in decision.ContentIdentities)
        {
            var action = decision.Decision == "confirm" ? "confirmed" : decision.Decision == "reject" ? "rejected" : "restored";
            // One explicit review resolves every equivalent ID for this owner/content. Old causal operations remain preserved.
            foreach (var label in taxonomy.Labels.Where(l => graph.CanonicalId(l.Id) == canonical))
            {
                ClassificationOwnershipReview.Record(database, _transaction!, image.Key, label.Id, action);
                database.RecordPhotoClassificationDecision(image.Key, label.Id, decision.Decision == "confirm" ? "1" : "0", true, _transaction!);
                database.RecordPhotoClassificationDecision(image.Key, label.Id, decision.Decision == "confirm" ? "accepted" : action, false, _transaction!);
            }
        }
        Execute("INSERT INTO classification_v2_changes VALUES($id,$hash,$result)", ("$id", decision.OperationId), ("$hash", hash), ("$result", Encode(new { Count = decision.ContentIdentities.Count })));
        return decision.ContentIdentities.Count;
    });

    private Dictionary<(string Owner, string Identity), string> ManualFingerprints()
    {
        var values = new Dictionary<(string, string), List<string>>();
        using var command = Sql("""
            SELECT b.owner_key,b.content_identity,e.operation_id,e.kind,e.field,e.value
            FROM photo_metadata_operation_bindings b JOIN metadata_edits e ON e.operation_id=b.operation_id
            WHERE e.kind IN ('photo-classification-confirmation','photo-classification-policy')
            ORDER BY b.owner_key,b.content_identity,e.operation_id,e.kind,e.field
            """);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = (reader.GetString(0), reader.GetString(1));
            if (!values.TryGetValue(key, out var edits)) values[key] = edits = [];
            edits.Add(Encode(new[] { reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5) }));
        }
        return values.ToDictionary(item => item.Key, item => Digest(string.Join('\n', item.Value)));
    }
}
