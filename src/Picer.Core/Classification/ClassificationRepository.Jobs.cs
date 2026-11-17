namespace Picer.Core.Classification;

public sealed partial class ClassificationRepository
{
    public bool HasRunnableWork() => Scalar("""
        SELECT CASE WHEN EXISTS(SELECT 1 FROM classification_v2_jobs WHERE state IN ('queued','running'))
          OR EXISTS(SELECT 1 FROM classification_v2_cluster_tasks WHERE state IN ('pending','leased')) THEN 1 ELSE 0 END
        """) == "1";
    public ClassificationJob Start(string planId, string requestId, string? actor = null)
    {
        ValidateRequestId(requestId);
        return Write(() =>
        {
            var existing = Scalar("SELECT id FROM classification_v2_jobs WHERE request_id=$request OR plan_id=$plan", ("$request", requestId), ("$plan", planId));
            if (existing is not null) return Job(existing);
            var plan = Decode<ClassificationPlan>(Scalar("SELECT payload FROM classification_v2_plans WHERE id=$id", ("$id", planId)) ?? throw new ArgumentException("classification_plan_missing"));
            if (plan.ExpiresAt < DateTimeOffset.UtcNow || plan.Epoch != Epoch || plan.TaxonomyRevision != Taxonomy().Revision)
                throw new InvalidOperationException("classification_plan_stale");
            var configuration = Configuration();
            if (configuration != plan.Configuration || !configuration.Enabled) throw new InvalidOperationException("classification_configuration_changed");
            if (plan.InputRevision != long.Parse(Scalar("SELECT input_revision FROM classification_v2_revision WHERE id=1")!, System.Globalization.CultureInfo.InvariantCulture))
                throw new InvalidOperationException("classification_plan_stale");
            var id = Guid.NewGuid().ToString("N");
            Execute("INSERT INTO classification_v2_jobs VALUES($id,$request,$plan,'queued',$config,$budget,0,$now,$now,$actor)",
                ("$id", id), ("$request", requestId), ("$plan", planId), ("$config", Encode(configuration)), ("$budget", configuration.MaximumCalls), ("$now", Now), ("$actor", actor));
            foreach (var item in plan.Items)
                Execute("""
                    INSERT INTO classification_v2_items(job_id,image_id,fingerprint,input,state)
                    VALUES($job,$image,$fingerprint,$input,CASE WHEN EXISTS(
                        SELECT 1 FROM classification_v2_items WHERE image_id=$image AND state IN ('pending','leased')) THEN 'waiting' ELSE 'pending' END)
                    """, ("$job", id), ("$image", item.ImageId), ("$fingerprint", item.Fingerprint), ("$input", Encode(item.Input)));
            FinishJobs();
            return Job(id);
        });
    }

    public ClassificationJob Job(string id)
    {
        using var command = Sql("""
            SELECT j.id,j.state,COUNT(i.image_id),COALESCE(SUM(i.state IN ('succeeded','analyzed_unclassified')),0),
                COALESCE(SUM(i.state IN ('failed','superseded')),0),COALESCE(SUM(i.state IN ('pending','waiting')),0),
                COALESCE(SUM(i.state='leased'),0),j.used,j.budget
            FROM classification_v2_jobs j LEFT JOIN classification_v2_items i ON i.job_id=j.id WHERE j.id=$id GROUP BY j.id
            """, ("$id", id));
        using var r = command.ExecuteReader();
        if (!r.Read()) throw new ArgumentException("classification_job_missing");
        return new(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8));
    }

    public ClassificationJob? LatestJob()
    {
        var id = Scalar("SELECT id FROM classification_v2_jobs ORDER BY created_utc DESC,id DESC LIMIT 1");
        return id is null ? null : Job(id);
    }

    public void Control(string id, string operation, string? actor = null) => Write(() =>
    {
        if (operation is not ("pause" or "resume" or "cancel")) throw new ArgumentException("classification_operation_invalid");
        var job = Job(id);
        if (job.State is "completed" or "partial_failed" or "cancelled") return 0;
        if (operation == "resume")
        {
            var config = Configuration();
            if (config is null || !config.Enabled || Encode(config) != Scalar("SELECT config FROM classification_v2_jobs WHERE id=$id", ("$id", id)))
                throw new InvalidOperationException("classification_configuration_changed");
            if (job.UsedCalls >= job.MaximumCalls) throw new InvalidOperationException("classification_budget_exhausted");
        }
        Execute("UPDATE classification_v2_jobs SET state=$state,updated_utc=$now WHERE id=$id", ("$state", operation == "cancel" ? "cancelled" : operation == "pause" ? "paused" : "queued"), ("$id", id), ("$now", Now));
        if (operation == "resume") Execute("UPDATE classification_v2_jobs SET actor=$actor WHERE id=$id", ("$actor", actor), ("$id", id));
        if (operation == "cancel") Execute("UPDATE classification_v2_items SET state='cancelled',lease=NULL WHERE job_id=$id AND state IN ('pending','waiting','leased')", ("$id", id));
        return 0;
    });

    public ClassificationLease? Claim() => Write(() =>
    {
        Execute("UPDATE classification_v2_items SET state='pending',lease=NULL,lease_until=NULL WHERE state='leased' AND lease_until<$now", ("$now", Now));
        Execute("""
            UPDATE classification_v2_items SET state=(SELECT CASE WHEN outcome='analyzed_unclassified' THEN 'analyzed_unclassified' ELSE 'succeeded' END FROM classification_v2_work WHERE image_id=classification_v2_items.image_id)
            WHERE state IN ('pending','waiting') AND EXISTS(SELECT 1 FROM classification_v2_work w WHERE w.image_id=classification_v2_items.image_id AND w.success_fingerprint=classification_v2_items.fingerprint)
            """);
        // Waiting successors become eligible only after the previous lease has completed.
        using (var command = Sql("SELECT i.job_id,i.image_id FROM classification_v2_items i JOIN classification_v2_jobs j ON j.id=i.job_id WHERE i.state='waiting' AND j.state IN ('queued','running') ORDER BY i.job_id,i.image_id"))
        {
            var candidates = new List<(string Job, long Image)>();
            using (var r = command.ExecuteReader()) while (r.Read()) candidates.Add((r.GetString(0), r.GetInt64(1)));
            foreach (var candidate in candidates)
                Execute("UPDATE classification_v2_items SET state='pending' WHERE job_id=$job AND image_id=$image AND NOT EXISTS(SELECT 1 FROM classification_v2_items WHERE image_id=$image AND state IN ('pending','leased'))", ("$job", candidate.Job), ("$image", candidate.Image));
        }
        Execute("UPDATE classification_v2_jobs SET state='paused_budget',updated_utc=$now WHERE state IN ('running','queued') AND used>=budget", ("$now", Now));
        Execute("UPDATE classification_v2_items SET state='waiting' WHERE state='pending' AND job_id IN (SELECT id FROM classification_v2_jobs WHERE state='paused_budget')");
        FinishJobs();
        using var select = Sql("""
            SELECT i.job_id,i.image_id,i.fingerprint,i.input,j.config FROM classification_v2_items i
            JOIN classification_v2_jobs j ON j.id=i.job_id
            LEFT JOIN classification_v2_work w ON w.image_id=i.image_id
            WHERE j.state IN ('queued','running') AND i.state='pending' AND j.used<j.budget
              AND (w.retry_after IS NULL OR w.retry_after<=$now OR w.attempt_fingerprint<>i.fingerprint)
            ORDER BY j.created_utc,i.image_id LIMIT 1
            """, ("$now", Now));
        string job; long image; string fingerprint; ClassificationInput input; string config;
        using (var r = select.ExecuteReader())
        {
            if (!r.Read()) return null;
            job = r.GetString(0); image = r.GetInt64(1); fingerprint = r.GetString(2); input = Decode<ClassificationInput>(r.GetString(3)); config = r.GetString(4);
        }
        if (Configuration() is not { Enabled: true } settings || Encode(settings) != config)
        { Execute("UPDATE classification_v2_jobs SET state='paused' WHERE id=$id", ("$id", job)); return null; }
        var token = Guid.NewGuid().ToString("N");
        Execute("UPDATE classification_v2_items SET state='leased',lease=$lease,lease_until=$until,attempts=attempts+1 WHERE job_id=$job AND image_id=$image", ("$lease", token), ("$until", DateTimeOffset.UtcNow.AddMinutes(6).ToString("O")), ("$job", job), ("$image", image));
        Execute("UPDATE classification_v2_jobs SET state='running',used=used+1,updated_utc=$now WHERE id=$id", ("$id", job), ("$now", Now));
        return new ClassificationLease(job, image, token, fingerprint, input, Scalar("SELECT actor FROM classification_v2_jobs WHERE id=$id", ("$id", job)));
    });

    public ClassificationConfiguration JobConfiguration(string id) => Decode<ClassificationConfiguration>(
        Scalar("SELECT config FROM classification_v2_jobs WHERE id=$id", ("$id", id)) ?? throw new ArgumentException("classification_job_missing"));

    public bool Publish(ClassificationLease lease, ClassificationResult result) => Write(() =>
    {
        if (!Owns(lease)) return false;
        var configuration = JobConfiguration(lease.JobId);
        var context = Contexts(configuration, lease.ImageId).SingleOrDefault();
        if (context is null || ClassificationInputFingerprint.Compute(context.Input) != lease.Fingerprint
            || configuration != Configuration())
        {
            SupersedeCore(lease, context, configuration, modelCalled: true); return false;
        }
        ClassificationOutputValidator.Validate(result, context, Taxonomy());
        var outcome = StoreResult(lease.ImageId, lease.Fingerprint, context, result);
        SetItem(lease, outcome, null); FinishJobs(); return true;
    });

    private string StoreResult(long imageId, string fingerprint, ClassificationContext context, ClassificationResult result)
    {
        var supported = result.Labels.Where(l => l.Support == "supported").Select(l => l.LabelId).ToArray();
        Execute("""
            INSERT INTO classification_v2_results VALUES($image,$fingerprint,$input,$payload,$now)
            ON CONFLICT(image_id) DO UPDATE SET fingerprint=excluded.fingerprint,input=excluded.input,payload=excluded.payload,updated_utc=excluded.updated_utc
            """, ("$image", imageId), ("$fingerprint", fingerprint), ("$input", Encode(context.Input)), ("$payload", Encode(result)), ("$now", Now));
        Execute("DELETE FROM classification_v2_labels WHERE image_id=$image", ("$image", imageId));
        foreach (var label in supported)
            Execute("INSERT INTO classification_v2_labels VALUES($image,$label)", ("$image", imageId), ("$label", label));
        var outcome = supported.Length == 0 ? "analyzed_unclassified" : "succeeded";
        Execute("""
            INSERT INTO classification_v2_work(image_id,success_input,success_fingerprint,attempt_fingerprint,outcome)
            VALUES($image,$input,$fingerprint,$fingerprint,$outcome)
            ON CONFLICT(image_id) DO UPDATE SET success_input=excluded.success_input,success_fingerprint=excluded.success_fingerprint,
                attempt_fingerprint=excluded.attempt_fingerprint,outcome=excluded.outcome,error_code=NULL,retry_after=NULL
            """, ("$image", imageId), ("$input", Encode(context.Input)), ("$fingerprint", fingerprint), ("$outcome", outcome));
        foreach (var proposal in result.Proposals) StoreProposal(context.Input.OwnerId, proposal, [imageId]);
        return outcome;
    }

    public void Fail(ClassificationLease lease, string code, bool retryable) => Write(() =>
    {
        if (!Owns(lease)) return 0;
        var configuration = JobConfiguration(lease.JobId);
        var context = Contexts(configuration, lease.ImageId).SingleOrDefault();
        if (context is null || ClassificationInputFingerprint.Compute(context.Input) != lease.Fingerprint || configuration != Configuration())
        { SupersedeCore(lease, context, configuration, modelCalled: true); return 0; }
        if (code.Length > 80 || code.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) code = "classification_failed";
        var attempts = int.Parse(Scalar("SELECT attempts FROM classification_v2_items WHERE job_id=$job AND image_id=$image", ("$job", lease.JobId), ("$image", lease.ImageId))!, System.Globalization.CultureInfo.InvariantCulture);
        var retry = retryable && attempts < 3;
        Execute("""
            INSERT INTO classification_v2_work(image_id,attempt_fingerprint,outcome,error_code,retry_after) VALUES($image,$fingerprint,'failed',$code,$after)
            ON CONFLICT(image_id) DO UPDATE SET attempt_fingerprint=excluded.attempt_fingerprint,outcome=excluded.outcome,error_code=excluded.error_code,retry_after=excluded.retry_after
            """, ("$image", lease.ImageId), ("$fingerprint", lease.Fingerprint), ("$code", code), ("$after", retry ? DateTimeOffset.UtcNow.AddSeconds(10 * attempts).ToString("O") : null));
        SetItem(lease, retry ? "pending" : "failed", code); FinishJobs(); return 0;
    });

    private bool Owns(ClassificationLease lease) => Scalar("SELECT lease FROM classification_v2_items WHERE job_id=$job AND image_id=$image AND state='leased' AND lease_until>=$now", ("$job", lease.JobId), ("$image", lease.ImageId), ("$now", Now)) == lease.Token;
    public void BlockAuthorization(ClassificationLease lease, bool modelCalled = true) => Write(() =>
    {
        if (!Owns(lease)) return 0;
        SetItem(lease, "waiting", "classification_authorization_revoked");
        Execute("UPDATE classification_v2_items SET state='waiting' WHERE job_id=$job AND state='pending'", ("$job", lease.JobId));
        if (!modelCalled)
        {
            Execute("UPDATE classification_v2_jobs SET used=MAX(0,used-1) WHERE id=$id", ("$id", lease.JobId));
            Execute("UPDATE classification_v2_items SET attempts=MAX(0,attempts-1) WHERE job_id=$job AND image_id=$image", ("$job", lease.JobId), ("$image", lease.ImageId));
        }
        Execute("UPDATE classification_v2_jobs SET state='blocked_authorization',updated_utc=$now WHERE id=$id", ("$id", lease.JobId), ("$now", Now));
        return 0;
    });
    public void Supersede(ClassificationLease lease) => Write(() =>
    {
        if (!Owns(lease)) return 0;
        var configuration = JobConfiguration(lease.JobId);
        SupersedeCore(lease, Contexts(configuration, lease.ImageId).SingleOrDefault(), configuration, modelCalled: false);
        return 0;
    });
    private void SupersedeCore(ClassificationLease lease, ClassificationContext? current, ClassificationConfiguration configuration, bool modelCalled)
    {
        if (!modelCalled) Execute("UPDATE classification_v2_jobs SET used=MAX(0,used-1) WHERE id=$id", ("$id", lease.JobId));
        if (current is null || current.Input.SpaceEpoch != lease.Input.SpaceEpoch || current.Input.OwnerId != lease.Input.OwnerId)
            SetItem(lease, "superseded", "classification_scope_changed");
        else if (configuration != Configuration())
        {
            SetItem(lease, "pending", "classification_configuration_changed");
            Execute("UPDATE classification_v2_jobs SET state='paused' WHERE id=$id", ("$id", lease.JobId));
        }
        else
        {
            // Replace the obsolete leased item with a durable successor in the same budget envelope.
            Execute("UPDATE classification_v2_items SET fingerprint=$fingerprint,input=$input,state='pending',lease=NULL,lease_until=NULL,attempts=0,error_code=NULL WHERE job_id=$job AND image_id=$image AND lease=$lease",
                ("$fingerprint", ClassificationInputFingerprint.Compute(current.Input)), ("$input", Encode(current.Input)), ("$job", lease.JobId), ("$image", lease.ImageId), ("$lease", lease.Token));
        }
        FinishJobs();
    }
    private void SetItem(ClassificationLease lease, string state, string? code) => Execute("UPDATE classification_v2_items SET state=$state,error_code=$code,lease=NULL,lease_until=NULL WHERE job_id=$job AND image_id=$image AND lease=$lease", ("$state", state), ("$code", code), ("$job", lease.JobId), ("$image", lease.ImageId), ("$lease", lease.Token));
    private void FinishJobs() => Execute("""
        UPDATE classification_v2_jobs SET state=CASE WHEN EXISTS(SELECT 1 FROM classification_v2_items i WHERE i.job_id=classification_v2_jobs.id AND i.state IN ('failed','superseded')) THEN 'partial_failed' ELSE 'completed' END,updated_utc=$now
        WHERE state IN ('queued','running','paused_budget') AND NOT EXISTS(SELECT 1 FROM classification_v2_items i WHERE i.job_id=classification_v2_jobs.id AND i.state IN ('pending','leased','waiting'))
        """, ("$now", Now));
}
