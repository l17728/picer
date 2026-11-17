namespace Picer.Core.Classification;

public sealed partial class ClassificationRepository
{
    private void StoreProposal(string owner, ClassificationProposalDraft draft, IReadOnlyList<long> images)
    {
        // Identical drafts may accumulate evidence; semantic merging remains an LLM/reviewer decision.
        var id = Digest(Encode(new { owner, draft.Name, draft.Dimension, draft.Definition, draft.Exclusions, Parents = draft.ParentIds.Order(StringComparer.Ordinal) }));
        var previous = Scalar("SELECT payload FROM classification_v2_proposals WHERE id=$id", ("$id", id));
        var old = previous is null ? null : Decode<ClassificationProposal>(previous);
        if (old is { State: not "proposed" }) return;
        var imageIds = images.Concat(old?.ImageIds ?? []).Distinct().Order().Take(24).ToArray();
        var fingerprints = old?.InputFingerprints?.ToDictionary(p => p.Key, p => p.Value) ?? new Dictionary<long, string>();
        var config = Configuration() ?? throw new InvalidOperationException("classification_configuration_required");
        foreach (var image in images)
            if (Contexts(config, image).SingleOrDefault() is { } context && context.Input.OwnerId == owner)
                fingerprints[image] = ProposalFingerprint(context.Input);
        var proposal = new ClassificationProposal(id, owner, (old?.Revision ?? 0) + 1, "proposed", draft,
            imageIds, fingerprints.Where(p => imageIds.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value));
        Execute("INSERT INTO classification_v2_proposals VALUES($id,$owner,$revision,'proposed',$payload,$now) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,payload=excluded.payload",
            ("$id", id), ("$owner", owner), ("$revision", proposal.Revision), ("$payload", Encode(proposal)), ("$now", Now));
    }

    public IReadOnlyList<ClassificationProposal> Proposals()
    {
        using var command = Sql("SELECT payload FROM classification_v2_proposals ORDER BY created_utc DESC,id LIMIT 500");
        using var reader = command.ExecuteReader();
        var result = new List<ClassificationProposal>();
        while (reader.Read())
        {
            var proposal = Decode<ClassificationProposal>(reader.GetString(0));
            result.Add(proposal);
        }
        return result;
    }

    public ClassificationTaxonomySnapshot Review(ClassificationProposalReview review) => Write(() =>
    {
        ValidateRequestId(review.OperationId);
        var requestHash = Digest(Encode(review));
        using (var lookup = Sql("SELECT payload_hash,result FROM classification_v2_changes WHERE operation_id=$id", ("$id", review.OperationId)))
        using (var r = lookup.ExecuteReader())
            if (r.Read())
            {
                if (r.GetString(0) != requestHash) throw new InvalidOperationException("classification_operation_conflict");
                return Decode<ClassificationTaxonomySnapshot>(r.GetString(1));
            }
        var taxonomy = Taxonomy();
        var proposal = Decode<ClassificationProposal>(Scalar("SELECT payload FROM classification_v2_proposals WHERE id=$id", ("$id", review.ProposalId)) ?? throw new ArgumentException("classification_proposal_missing"));
        if (proposal.Revision != review.ExpectedRevision || taxonomy.Revision != review.ExpectedTaxonomyRevision || proposal.State != "proposed")
            throw new InvalidOperationException("classification_review_conflict");
        if (review.Decision != "reject")
        {
            var config = Configuration() ?? throw new InvalidOperationException("classification_configuration_required");
            foreach (var image in proposal.ImageIds)
            {
                var context = Contexts(config, image).SingleOrDefault();
                if (context is null || context.Input.OwnerId != proposal.OwnerKey || proposal.InputFingerprints is null
                    || !proposal.InputFingerprints.TryGetValue(image, out var expected) || ProposalFingerprint(context.Input) != expected)
                    throw new InvalidOperationException("classification_proposal_stale");
            }
        }
        if (review.Decision == "accept")
        {
            var id = "dynamic." + proposal.Id;
            var draft = proposal.Draft;
            var label = new ClassificationLabel(id, draft.Dimension, draft.Name, draft.Definition, draft.Exclusions, []);
            var labels = taxonomy.Labels.Append(label).ToArray();
            // Review accepts navigation placement only; semantic IsA is never guessed from relatedness.
            var edges = taxonomy.Edges.Concat(draft.ParentIds.Select(parent => new ClassificationEdge(parent, id, TaxonomyEdgeKind.Browse))).ToArray();
            _ = new ClassificationTaxonomy(labels, edges);
            taxonomy = new(taxonomy.Revision + 1, taxonomy.DiscoveryRevision + 1, labels, edges, taxonomy.Definitions.Append(new(id, 1)).ToArray());
            Execute("INSERT INTO classification_v2_taxonomy VALUES(1,$revision,$discovery,$payload) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,discovery=excluded.discovery,payload=excluded.payload",
                ("$revision", taxonomy.Revision), ("$discovery", taxonomy.DiscoveryRevision), ("$payload", Encode(taxonomy)));
        }
        else if (review.Decision == "merge")
        {
            if (!taxonomy.Labels.Any(l => l.Id == review.MergeLabelId && l.Active && l.Assignable)) throw new ArgumentException("classification_merge_target_invalid");
            // A proposal has no published members or manual decisions. Merging never copies assignments.
        }
        else if (review.Decision != "reject") throw new ArgumentException("classification_review_invalid");
        proposal = proposal with { State = review.Decision, Revision = proposal.Revision + 1 };
        Execute("UPDATE classification_v2_proposals SET state=$state,revision=$revision,payload=$payload WHERE id=$id", ("$id", proposal.Id), ("$state", proposal.State), ("$revision", proposal.Revision), ("$payload", Encode(proposal)));
        Execute("INSERT INTO classification_v2_changes VALUES($id,$hash,$result)", ("$id", review.OperationId), ("$hash", requestHash), ("$result", Encode(taxonomy)));
        return taxonomy;
    });
    private static string ProposalFingerprint(ClassificationInput input) => ClassificationInputFingerprint.Compute(
        input with { Definitions = [], DiscoveryRevision = 0, CandidatePolicyVersion = "proposal-evidence-v1" });

    public ClassificationTaxonomySnapshot EditTaxonomy(ClassificationTaxonomyEdit edit) => Write(() =>
    {
        ValidateRequestId(edit.OperationId);
        var hash = Digest(Encode(edit));
        var oldHash = Scalar("SELECT payload_hash FROM classification_v2_changes WHERE operation_id=$id", ("$id", edit.OperationId));
        if (oldHash is not null)
        {
            if (hash != oldHash) throw new InvalidOperationException("classification_operation_conflict");
            return Decode<ClassificationTaxonomySnapshot>(Scalar("SELECT result FROM classification_v2_changes WHERE operation_id=$id", ("$id", edit.OperationId))!);
        }
        var taxonomy = Taxonomy();
        if (taxonomy.Revision != edit.ExpectedRevision) throw new InvalidOperationException("classification_review_conflict");
        var original = taxonomy.Labels.SingleOrDefault(l => l.Id == edit.LabelId) ?? throw new ArgumentException("classification_label_missing");
        if (original.RedirectTo is not null) throw new InvalidOperationException("classification_label_merged");
        if (edit.Name is not null && (string.IsNullOrWhiteSpace(edit.Name) || edit.Name.Length > 80)
            || edit.BrowseParentIds?.Count > 8) throw new ArgumentException("classification_taxonomy_edit_invalid");
        var updated = original with { Name = edit.Name?.Trim() ?? original.Name, Active = edit.Active ?? original.Active,
            Aliases = edit.Name is null ? original.Aliases : original.Aliases.Append(original.Name).Distinct().ToArray() };
        var labels = taxonomy.Labels.Select(l => l.Id == original.Id ? updated : l).ToArray();
        if (edit.MergeInto is not null)
        {
            var target = taxonomy.Labels.SingleOrDefault(l => l.Id == edit.MergeInto);
            if (!edit.ConfirmEquivalent || !original.Active || !original.Assignable || target is null || !target.Active
                || !target.Assignable || target.Id == original.Id || target.Dimension != original.Dimension
                || edit.Name is not null || edit.Active is not null || edit.BrowseParentIds is not null)
                throw new ArgumentException("classification_merge_requires_equivalence");
            labels = labels.Select(l => l.Id == original.Id ? l with { Active = false, RedirectTo = target.Id }
                : l.Id == target.Id ? l with { Aliases = l.Aliases.Concat(original.Aliases).Append(original.Name).Distinct().ToArray() } : l).ToArray();
        }
        var edges = edit.BrowseParentIds is null ? taxonomy.Edges : taxonomy.Edges
            .Where(e => e.ChildId != original.Id || e.Kind != TaxonomyEdgeKind.Browse)
            .Concat(edit.BrowseParentIds.Distinct().Select(parent => new ClassificationEdge(parent, original.Id, TaxonomyEdgeKind.Browse))).ToArray();
        _ = new ClassificationTaxonomy(labels, edges);
        var result = taxonomy with { Revision = taxonomy.Revision + 1, DiscoveryRevision = taxonomy.DiscoveryRevision + (original.Active == updated.Active && edit.MergeInto is null ? 0 : 1), Labels = labels, Edges = edges };
        Execute("INSERT INTO classification_v2_taxonomy VALUES(1,$revision,$discovery,$payload) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,discovery=excluded.discovery,payload=excluded.payload",
            ("$revision", result.Revision), ("$discovery", result.DiscoveryRevision), ("$payload", Encode(result)));
        Execute("INSERT INTO classification_v2_changes VALUES($id,$hash,$result)", ("$id", edit.OperationId), ("$hash", hash), ("$result", Encode(result)));
        return result;
    });
}
