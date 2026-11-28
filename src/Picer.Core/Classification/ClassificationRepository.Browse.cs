using System.Globalization;

namespace Picer.Core.Classification;

public sealed partial class ClassificationRepository
{
    private bool _captureReplica;
    private ClassificationReplica? _replica;
    public ClassificationReplica ExportReplica()
    {
        _captureReplica = true;
        try { _ = Browse(new(new([], []))); return _replica!; }
        finally { _captureReplica = false; _replica = null; }
    }
    public ClassificationBrowseResult Browse(ClassificationBrowseRequest request)
    {
        if (request.PageSize is < 1 or > 200) throw new ArgumentException("classification_page_size_invalid");
        using var read = database.BeginReadTransaction();
        _transaction = read;
        try
        {
            var taxonomy = Taxonomy();
            var namedPeople = new List<ClassificationLabel>();
            using (var people = Sql("SELECT id,display_name FROM people WHERE length(trim(display_name))>0 ORDER BY id"))
            using (var reader = people.ExecuteReader())
                while (reader.Read()) namedPeople.Add(new("person.confirmed." + reader.GetInt64(0).ToString(CultureInfo.InvariantCulture),
                    "subject", reader.GetString(1), "来自人物管理中的人工确认关联", "不使用模型猜测或未确认候选", []));
            if (namedPeople.Count > 0)
                taxonomy = taxonomy with
                {
                    Labels = taxonomy.Labels.Concat(new[] { new ClassificationLabel("nav.named_people", "subject", "已命名人物", "人物管理中的最新命名列表", "", [], Assignable: false) }).Concat(namedPeople).ToArray(),
                    Edges = taxonomy.Edges.Concat(new[] { new ClassificationEdge("subject.people", "nav.named_people", TaxonomyEdgeKind.Browse) })
                        .Concat(namedPeople.Select(p => new ClassificationEdge("nav.named_people", p.Id, TaxonomyEdgeKind.Browse))).ToArray()
                };
            var namedAssignments = ReadLabelSets("""
                SELECT DISTINCT f.image_id,'person.confirmed.'||a.person_id FROM detected_faces f
                JOIN face_person_assignments a ON a.face_id=f.id AND a.state='confirmed'
                JOIN people p ON p.id=a.person_id WHERE f.is_archived=0 AND length(trim(p.display_name))>0
                """);
            var graph = new ClassificationTaxonomy(taxonomy.Labels, taxonomy.Edges);
            var filter = new CompiledClassificationFilter(graph, request.Filter);
            var config = Configuration() ?? new(false, "unconfigured", "local", false);
            var contexts = Contexts(config);
            var computeFingerprint = ClassificationInputFingerprint.CreateBatchComputer(taxonomy.Definitions);
            var manualFingerprints = ManualFingerprints();
            var works = new Dictionary<long, (string? Fingerprint, string Outcome, string? Attempt)>();
            using (var command = Sql("SELECT image_id,success_fingerprint,outcome,attempt_fingerprint FROM classification_v2_work"))
            using (var r = command.ExecuteReader()) while (r.Read()) works[r.GetInt64(0)] = (r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3));
            var suggested = ReadLabelSets("SELECT image_id,label_id FROM classification_v2_labels");
            var confirmed = ReadLabelSets("SELECT image_id,concept_id FROM image_classification_assertions WHERE assertion_state='user_confirmed' AND review_state='accepted'");
            var legacy = ReadLabelSets("SELECT image_id,concept_id FROM image_classification_assertions WHERE assertion_state IN ('automatic','legacy_effective') AND review_state IN ('pending','accepted') AND source_kind='vision_model'");
            // Keep old description suggestions distinct from verified model output. Only current
            // description projections qualify; archived or changed-file evidence must not reappear.
            var legacySuggested = ReadLabelSets("""
                SELECT DISTINCT a.image_id,a.concept_id FROM image_classification_assertions a
                JOIN images i ON i.id=a.image_id
                JOIN cloud_vision_analyses d ON d.image_id=a.image_id
                WHERE a.assertion_state='suggested' AND a.review_state IN ('pending','accepted')
                  AND a.source_kind='vision_model' AND d.status='completed' AND d.result_json IS NOT NULL
                  AND d.source_file_size=i.file_size AND picer_same_instant(d.source_modified_utc,i.modified_utc)
                  AND a.source_version='existing-description-v1:'||d.provider||':'||d.model_version||':'||d.prompt_version
                  AND EXISTS (SELECT 1 FROM json_tree(d.result_json) e WHERE e.type='text' AND trim(e.value)=a.evidence_ref)
                """);
            var rejected = ReadLabelSets("""
                SELECT i.id,r.concept_id FROM images i JOIN image_classification_rejections r
                  ON r.image_id=i.id OR (lower(r.content_sha256)=lower(i.sha256) AND r.owner_key=(SELECT owner_key FROM sync_image_owners WHERE image_id=i.id))
                WHERE r.source_scope IN ('vision_model','all')
                """);
            var restored = ReadLabelSets("""
                SELECT i.id,e.field FROM images i JOIN sync_image_owners o ON o.image_id=i.id
                JOIN photo_metadata_operation_bindings b ON b.owner_key=o.owner_key
                JOIN metadata_edits e ON e.operation_id=b.operation_id
                WHERE e.kind='photo-classification-policy' AND e.value='restored'
                  AND (b.content_identity='sha256:'||lower(i.sha256) OR b.content_identity=
                    'local:'||(SELECT epoch FROM mirror_revision WHERE id=1)||':'||
                    (SELECT version_id FROM photo_metadata_file_versions WHERE image_id=i.id))
                """);
            var photos = new List<ClassificationPhoto>();
            var fingerprints = new List<string>();
            var confirmedCount = 0; var suggestedCount = 0; var legacyCount = 0; var without = 0;
            var unprocessed = 0; var pending = 0; var failed = 0; var empty = 0;
            var legacySuggestedCount = 0; var evidenceCount = 0;
            foreach (var context in contexts)
            {
                var id = context.Image.Id;
                var fingerprint = computeFingerprint(context.Input);
                fingerprints.Add(fingerprint);
                var work = works.GetValueOrDefault(id);
                var current = work.Fingerprint == fingerprint;
                var state = work.Outcome == "failed" && work.Attempt == fingerprint ? "failed" : current ? work.Outcome : work.Fingerprint is null ? "unprocessed" : "pending_update";
                switch (state) { case "failed": failed++; break; case "unprocessed": unprocessed++; break; case "pending_update": pending++; break; case "analyzed_unclassified": empty++; break; }
                var manual = Valid(confirmed.GetValueOrDefault(id));
                var machine = current ? Valid(suggested.GetValueOrDefault(id)) : [];
                var historic = Valid(legacy.GetValueOrDefault(id));
                var historicSuggestions = Valid(legacySuggested.GetValueOrDefault(id));
                // A confirmed, non-archived face proves presence, never a relationship, event or identity label.
                var evidenceLabels = Valid(context.Input.People.Count > 0 ? new HashSet<string> { "subject.people" } : null);
                evidenceLabels.UnionWith(Valid(namedAssignments.GetValueOrDefault(id)));
                var excluded = Valid(rejected.GetValueOrDefault(id));
                var conflicts = manual.Concat(Valid(restored.GetValueOrDefault(id))).Where(excluded.Contains).ToHashSet(StringComparer.Ordinal);
                manual.ExceptWith(conflicts);
                machine.ExceptWith(excluded); historic.ExceptWith(excluded);
                historicSuggestions.ExceptWith(excluded); evidenceLabels.ExceptWith(excluded);
                if (excluded.Contains("subject.people")) evidenceLabels.Clear();
                if (conflicts.Count > 0) state = "conflict_pending";
                if (manual.Count > 0) confirmedCount++;
                if (machine.Count > 0) suggestedCount++;
                if (historic.Count > 0) legacyCount++;
                if (historicSuggestions.Count > 0) legacySuggestedCount++;
                if (evidenceLabels.Count > 0) evidenceCount++;
                if (manual.Count + machine.Count + evidenceLabels.Count == 0) without++;
                var all = new HashSet<string>(manual, StringComparer.Ordinal);
                all.UnionWith(evidenceLabels);
                if (request.IncludeSuggestions) all.UnionWith(machine);
                if (request.IncludeLegacy) all.UnionWith(historic);
                if (request.IncludeLegacy && request.IncludeSuggestions) all.UnionWith(historicSuggestions);
                photos.Add(new(id, context.Image.Path, context.Image.ThumbPath, all.Order(StringComparer.Ordinal).ToArray(), state, context.Input.MediaIdentity, context.Input.OwnerId,
                    manual.Order(StringComparer.Ordinal).ToArray(), machine.Order(StringComparer.Ordinal).ToArray(), historic.Order(StringComparer.Ordinal).ToArray(), conflicts.Order(StringComparer.Ordinal).ToArray(),
                    manualFingerprints.GetValueOrDefault((context.Input.OwnerId, context.Input.MediaIdentity), Digest("")),
                    historicSuggestions.Order(StringComparer.Ordinal).ToArray(), evidenceLabels.Order(StringComparer.Ordinal).ToArray()));
            }
            var counts = CountLabels(graph, photos);
            var proposalRevision = Scalar("SELECT group_concat(id||':'||revision,',') FROM (SELECT id,revision FROM classification_v2_proposals ORDER BY id)");
            var snapshot = Digest(Encode(new { Epoch, Taxonomy = taxonomy.Revision, NamedPeople = namedPeople, Fingerprints = fingerprints, Photos = photos, Proposals = proposalRevision }));
            if (request.Snapshot is not null && request.Snapshot != snapshot) throw new InvalidOperationException("classification_snapshot_stale");
            long after = 0;
            if (request.Cursor is not null)
            {
                var parts = request.Cursor.Split(':');
                if (parts.Length != 2 || parts[0] != snapshot || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out after))
                    throw new InvalidOperationException("classification_cursor_stale");
            }
            var matched = photos.Where(photo => filter.Matches(photo.LabelIds)).ToArray();
            var page = matched.Where(photo => photo.ImageId > after).Take(request.PageSize + 1).ToArray();
            var next = page.Length > request.PageSize ? snapshot + ":" + page[request.PageSize - 1].ImageId.ToString(CultureInfo.InvariantCulture) : null;
            var coverage = new ClassificationCoverage(photos.Count, confirmedCount, suggestedCount, legacyCount, without,
                unprocessed, pending, failed, empty, contexts.Count(c => c.Input.Descriptions.Count == 0), contexts.Count(c => c.Input.People.Count > 0), legacySuggestedCount, evidenceCount);
            if (_captureReplica) _replica = new(Epoch, snapshot, taxonomy, photos, Proposals(), coverage);
            return new(snapshot, taxonomy, coverage, counts, matched.Length, page.Take(request.PageSize).ToArray(), next) { Epoch = Epoch };

            HashSet<string> Valid(HashSet<string>? ids) => (ids ?? []).Where(graph.Labels.ContainsKey).Select(graph.CanonicalId)
                .Where(id => graph.Labels[id].Assignable && graph.Labels[id].Active).ToHashSet(StringComparer.Ordinal);
        }
        finally { _transaction = null; }
    }

    private Dictionary<long, HashSet<string>> ReadLabelSets(string sql)
    {
        var result = new Dictionary<long, HashSet<string>>();
        using var command = Sql(sql);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            if (!result.TryGetValue(id, out var labels)) result[id] = labels = new(StringComparer.Ordinal);
            labels.Add(reader.GetString(1));
        }
        return result;
    }
}
