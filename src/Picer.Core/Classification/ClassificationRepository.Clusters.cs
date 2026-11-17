namespace Picer.Core.Classification;

public sealed partial class ClassificationRepository
{
    public ClassificationClusterStatus? LatestCluster()
    {
        using var command = Sql("SELECT id,state,error_code FROM classification_v2_cluster_tasks ORDER BY created_utc DESC,rowid DESC LIMIT 1");
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)) : null;
    }

    public ClassificationClusterTask PlanCluster(ClassificationClusterRequest request, string? actor = null)
    {
        ValidateRequestId(request.RequestId);
        if (request.ImageIds.Count is < 2 or > 24 || request.ImageIds.Distinct().Count() != request.ImageIds.Count)
            throw new ArgumentException("classification_cluster_size_invalid");
        var config = Configuration() ?? throw new InvalidOperationException("classification_configuration_required");
        if (!config.Enabled) throw new InvalidOperationException("classification_not_enabled");
        var wanted = request.ImageIds.ToHashSet();
        var contexts = Contexts(config).Where(c => wanted.Contains(c.Image.Id)).OrderBy(c => c.Image.Id).ToArray();
        if (contexts.Length != wanted.Count || contexts.Select(c => c.Input.OwnerId).Distinct().Count() != 1)
            throw new ArgumentException("classification_cluster_scope_invalid");
        // Stratify across the ordered candidate set, including both boundaries; unsent members are never assigned.
        var representatives = contexts.Length <= 8 ? contexts : Enumerable.Range(0, 8)
            .Select(i => contexts[(int)Math.Round(i * (contexts.Length - 1) / 7d)]).ToArray();
        return Write(() =>
        {
            var old = Scalar("SELECT payload FROM classification_v2_cluster_tasks WHERE request_id=$request", ("$request", request.RequestId));
            if (old is not null) return Decode<ClassificationClusterTask>(old);
            var task = new ClassificationClusterTask(Guid.NewGuid().ToString("N"), "pending", Epoch, Taxonomy().Revision, config, representatives, contexts.Length, actor);
            Execute("INSERT INTO classification_v2_cluster_tasks VALUES($id,$request,'pending',$payload,NULL,NULL,NULL,$now)",
                ("$id", task.Id), ("$request", request.RequestId), ("$payload", Encode(task)), ("$now", Now));
            return task;
        });
    }

    public (ClassificationClusterTask Task, string Lease)? ClaimCluster() => Write<(ClassificationClusterTask, string)?>(() =>
    {
        Execute("UPDATE classification_v2_cluster_tasks SET state='failed',error_code='classification_cluster_interrupted' WHERE state='leased' AND lease_until<$now", ("$now", Now));
        var payload = Scalar("SELECT payload FROM classification_v2_cluster_tasks WHERE state='pending' ORDER BY created_utc LIMIT 1");
        if (payload is null) return null;
        var task = Decode<ClassificationClusterTask>(payload);
        foreach (var saved in task.Representatives)
        {
            var current = Contexts(task.Configuration, saved.Image.Id).SingleOrDefault();
            if (current is null || ClassificationInputFingerprint.Compute(current.Input) != ClassificationInputFingerprint.Compute(saved.Input))
            { Execute("UPDATE classification_v2_cluster_tasks SET state='failed',error_code='classification_cluster_stale' WHERE id=$id", ("$id", task.Id)); return null; }
        }
        if (task.Configuration != Configuration() || !task.Configuration.Enabled || task.Epoch != Epoch || task.TaxonomyRevision != Taxonomy().Revision)
        { Execute("UPDATE classification_v2_cluster_tasks SET state='failed',error_code='classification_cluster_stale' WHERE id=$id", ("$id", task.Id)); return null; }
        var lease = Guid.NewGuid().ToString("N");
        Execute("UPDATE classification_v2_cluster_tasks SET state='leased',lease=$lease,lease_until=$until WHERE id=$id", ("$id", task.Id), ("$lease", lease), ("$until", DateTimeOffset.UtcNow.AddMinutes(6).ToString("O")));
        return (task, lease);
    });

    public void CompleteCluster(ClassificationClusterTask task, string lease, ClassificationClusterResult result) => Write(() =>
    {
        if (Scalar("SELECT lease FROM classification_v2_cluster_tasks WHERE id=$id AND state='leased' AND lease_until>=$now", ("$id", task.Id), ("$now", Now)) != lease)
            return 0;
        if (task.Epoch != Epoch || task.TaxonomyRevision != Taxonomy().Revision || task.Configuration != Configuration())
            throw new InvalidOperationException("classification_cluster_stale");
        foreach (var context in task.Representatives)
        {
            var current = Contexts(task.Configuration, context.Image.Id).SingleOrDefault();
            if (current is null || ClassificationInputFingerprint.Compute(current.Input) != ClassificationInputFingerprint.Compute(context.Input))
                throw new InvalidOperationException("classification_cluster_stale");
        }
        if (result.SchemaVersion != "classification-cluster-v1" || result.EvaluatedImageIds is null || result.Proposals is null
            || result.Proposals.Count > 4 || !result.EvaluatedImageIds.Order().SequenceEqual(task.Representatives.Select(c => c.Image.Id).Order()))
            throw new InvalidDataException("classification_cluster_invalid");
        var taxonomy = Taxonomy();
        foreach (var proposed in result.Proposals)
        {
            if (proposed.SupportedImageIds is null || proposed.SupportedImageIds.Count < 2
                || proposed.SupportedImageIds.Distinct().Count() != proposed.SupportedImageIds.Count
                || proposed.SupportedImageIds.Any(id => !result.EvaluatedImageIds.Contains(id)))
                throw new InvalidDataException("classification_cluster_support_invalid");
            var proposal = proposed.Draft;
            // Validate shape and parent scope with the same rules as photo proposals.
            var context = task.Representatives.Single(c => c.Image.Id == proposed.SupportedImageIds[0]);
            var validation = new ClassificationResult("classification-result-v1", ClassificationInputFingerprint.Compute(context.Input),
                taxonomy.Labels.Where(l => l.Active && l.Assignable).Select(l => new ClassificationLabelEvaluation(l.Id, "uncertain", [])).ToArray(), [proposal]);
            ClassificationOutputValidator.Validate(validation, context, taxonomy);
            StoreProposal(context.Input.OwnerId, proposal, proposed.SupportedImageIds);
        }
        Execute("UPDATE classification_v2_cluster_tasks SET state='completed',lease=NULL WHERE id=$id AND lease=$lease", ("$id", task.Id), ("$lease", lease));
        return 0;
    });
    public void FailCluster(string id, string lease) => Execute("UPDATE classification_v2_cluster_tasks SET state='failed',error_code='classification_cluster_failed',lease=NULL WHERE id=$id AND lease=$lease", ("$id", id), ("$lease", lease));
}
