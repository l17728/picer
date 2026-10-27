using Picer.Core.Identity;

namespace Picer.IndexService.Identity;

public sealed partial class SpaceSyncReceiver
{
    public SpaceInspectionJob CreateInspection(SpaceInspectionRequest request)
    {
        if (!device.Allows("index.write")) throw new UnauthorizedAccessException();
        if (request is null || !Guid.TryParseExact(request.OperationId, "N", out _)) throw new ArgumentException("invalid_inspection_request");
        using (var transaction = database.BeginTransaction())
        {
            _ = SpaceEpoch();
            var existing = Scalar("SELECT job_id FROM sync_inspection_jobs WHERE operation_id=$operation", ("$operation", request.OperationId)) as string;
            if (existing is not null)
            {
                transaction.Commit(); return ReadInspection(new(existing));
            }
            if (Convert.ToInt64(Scalar("SELECT COUNT(*) FROM sync_inspection_jobs WHERE state='active'")) >= 20)
                throw new InvalidOperationException("too_many_active_inspections");
            var job = request.OperationId;
            Execute("INSERT INTO sync_inspection_jobs(job_id,operation_id,requester_grant,created_utc) VALUES($id,$operation,$grant,$now)",
                ("$id", job), ("$operation", request.OperationId), ("$grant", device.GrantId), ("$now", DateTime.UtcNow.ToString("O")));
            Execute("""
                INSERT INTO sync_inspection_targets(target_id,job_id,publisher_id,device_id,device_grant_id,updated_utc)
                SELECT lower(hex(randomblob(16))),$id,publisher_id,incarnation_id,device_grant_id,$now FROM sync_publishers
                WHERE space_id=$space AND state IN ('receiving','rebaseline');
                """, ("$id", job), ("$now", DateTime.UtcNow.ToString("O")), ("$space", device.SpaceId));
            Execute("""
                INSERT INTO sync_inspection_targets(target_id,job_id,publisher_id,device_id,device_grant_id,state,updated_utc,error_code)
                SELECT lower(hex(randomblob(16))),$id,'legacy-source:'||s.id,'legacy-source:'||s.id,'','unbound',$now,'source_not_bound'
                FROM sources s WHERE NOT EXISTS(SELECT 1 FROM sync_source_bindings b WHERE b.source_id=s.id)
                    AND (EXISTS(SELECT 1 FROM images i WHERE i.source_id=s.id) OR EXISTS(SELECT 1 FROM videos v WHERE v.source_id=s.id));
                """,("$id",job),("$now",DateTime.UtcNow.ToString("O")));
            transaction.Commit();
        }
        var id = (string)Scalar("SELECT job_id FROM sync_inspection_jobs WHERE operation_id=$operation", ("$operation", request.OperationId))!;
        return ReadInspection(new(id));
    }

    public SpaceInspectionJob ReadInspection(SpaceInspectionJobRequest request, Func<string, bool>? grantActive = null,
        Func<string,string?>? grantName = null)
    {
        if (!device.Allows("index.read")) throw new UnauthorizedAccessException();
        if (request is null || !Guid.TryParseExact(request.JobId, "N", out _)) throw new ArgumentException("invalid_inspection_job");
        using var transaction = database.BeginTransaction();
        if (Scalar("SELECT 1 FROM sync_inspection_jobs WHERE job_id=$id", ("$id", request.JobId)) is null)
            throw new ArgumentException("inspection_not_found");
        var targets = new List<(string Id, string Publisher, string Device, string Grant, string State, string Updated, string? Error, string? Seen, string? Success)>();
        using (var query = Sql("""
            SELECT t.target_id,t.publisher_id,t.device_id,t.device_grant_id,
                CASE WHEN p.publisher_id IS NULL AND t.state IN ('queued','notified','running') THEN 'revoked' ELSE t.state END,
                t.updated_utc,t.error_code,s.last_seen_utc,
                (SELECT MAX(done.updated_utc) FROM sync_inspection_targets done WHERE done.publisher_id=t.publisher_id AND done.state='completed')
            FROM sync_inspection_targets t LEFT JOIN sync_publishers p ON p.publisher_id=t.publisher_id
            LEFT JOIN sync_source_presence s ON s.publisher_id=p.publisher_id
            WHERE t.job_id=$id ORDER BY t.target_id
            """, ("$id", request.JobId)))
        using (var reader = query.ExecuteReader())
            while (reader.Read()) targets.Add((reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),
                reader.GetString(4),reader.GetString(5),reader.IsDBNull(6) ? null : reader.GetString(6),reader.IsDBNull(7) ? null : reader.GetString(7),reader.IsDBNull(8) ? null : reader.GetString(8)));
        var result = new List<SpaceInspectionTarget>();
        foreach (var target in targets)
        {
            var state = target.State;
            if (state=="revoked" || state is "queued" or "notified" or "running" && grantActive is not null && !grantActive(target.Grant))
            {
                state = "revoked";
                Execute("UPDATE sync_inspection_targets SET state='revoked',updated_utc=$now,error_code='device_revoked' WHERE target_id=$id AND state IN ('queued','notified','running')",
                    ("$now", DateTime.UtcNow.ToString("O")), ("$id", target.Id));
            }
            var visible = state is "queued" or "notified" or "running" &&
                (!DateTime.TryParse(target.Seen, out var seen) || seen.ToUniversalTime() < DateTime.UtcNow.AddSeconds(-60)) ? "offline" : state;
            var name=grantName?.Invoke(target.Grant);
            const string legacyPrefix="legacy-source:";
            if (state=="unbound" && target.Device.StartsWith(legacyPrefix,StringComparison.Ordinal) &&
                long.TryParse(target.Device.AsSpan(legacyPrefix.Length),out var sourceId))
            {
                var path=Scalar("SELECT path FROM sources WHERE id=$id",("$id",sourceId)) as string;
                if (path is not null)
                {
                    if (Uri.TryCreate(path,UriKind.Absolute,out var uri) && !uri.IsFile) path=uri.AbsolutePath;
                    name="服务端来源："+path.Replace('\\','/').TrimEnd('/').Split('/').Last();
                }
            }
            result.Add(new(target.Id,target.Publisher,target.Device,visible,target.Updated,
                state == "revoked" ? "device_revoked" : target.Error,name,target.Success));
        }
        var current = (string)Scalar("SELECT state FROM sync_inspection_jobs WHERE job_id=$id", ("$id", request.JobId))!;
        var pendingReviews = Convert.ToInt64(Scalar("SELECT (SELECT COUNT(*) FROM video_metadata_ownership_reviews WHERE state='pending')+(SELECT COUNT(*) FROM video_face_ownership_reviews WHERE state='pending')"));
        var pendingConflicts = Convert.ToInt64(Scalar("SELECT COUNT(*) FROM metadata_edit_conflicts WHERE state='pending'"))
            +database.CountPendingCatalogSynchronization();
        var completed = result.All(target => target.State == "completed");
        if (current != "cancelled" && result.All(target => target.State is "completed" or "failed" or "revoked" or "cancelled" or "unbound"))
        {
            current = completed ? (pendingReviews > 0 || pendingConflicts > 0 ? "pending_review" : "completed") : "partially_failed";
            Execute("UPDATE sync_inspection_jobs SET state=$state WHERE job_id=$id", ("$state", current), ("$id", request.JobId));
        }
        transaction.Commit(); return new(request.JobId, current, result, pendingReviews, pendingConflicts);
    }

    public SpaceInspectionJob CancelInspection(SpaceInspectionJobRequest request)
    {
        if (!device.Allows("index.write")) throw new UnauthorizedAccessException();
        if (request is null || !Guid.TryParseExact(request.JobId, "N", out _)) throw new ArgumentException("invalid_inspection_job");
        using (var transaction = database.BeginTransaction())
        {
            Execute("UPDATE sync_inspection_jobs SET state='cancelled' WHERE job_id=$id AND state='active'", ("$id", request.JobId));
            Execute("UPDATE sync_inspection_targets SET state='cancelled',updated_utc=$now WHERE job_id=$id AND state IN ('queued','notified','running')",
                ("$id", request.JobId), ("$now", DateTime.UtcNow.ToString("O")));
            transaction.Commit();
        }
        return ReadInspection(request);
    }

    public SpaceNotification ReadNotification(SpaceNotificationRequest request)
    {
        if (!device.Allows("index.read") || device.Kind != "gui") throw new UnauthorizedAccessException();
        if (request is null || request.After < -1 || request.WaitSeconds is < 0 or > 25 ||
            request.LibraryId is not null && !Guid.TryParseExact(request.LibraryId, "N", out _)) throw new ArgumentException("invalid_notification_request");
        using var transaction = database.BeginTransaction();
        var epoch = SpaceEpoch();
        var revision = Convert.ToInt64(Scalar("SELECT revision+metadata_revision+(SELECT change_sequence FROM sync_space_state WHERE id=1) FROM mirror_revision WHERE id=1"));
        if (request.LibraryId is null) { transaction.Commit(); return new(null,epoch,revision,[]); }
        if (!device.Allows("index.write")) throw new UnauthorizedAccessException();
        var publisher = Scalar("SELECT publisher_id FROM sync_publishers WHERE library_id=$library AND incarnation_id=$device AND device_grant_id=$grant AND space_id=$space AND state IN ('receiving','rebaseline')",
            ("$library", request.LibraryId), ("$device", device.DeviceId), ("$grant", device.GrantId), ("$space", device.SpaceId)) as string
            ?? throw new InvalidOperationException("source_not_registered");
        revision = Convert.ToInt64(Scalar("SELECT COALESCE((SELECT revision FROM sync_publisher_notifications WHERE publisher_id=$publisher),0)", ("$publisher",publisher)));
        Execute("INSERT INTO sync_source_presence(publisher_id,last_seen_utc) VALUES($publisher,$now) ON CONFLICT(publisher_id) DO UPDATE SET last_seen_utc=excluded.last_seen_utc WHERE sync_source_presence.last_seen_utc<$cutoff",
            ("$publisher", publisher), ("$now", DateTime.UtcNow.ToString("O")),("$cutoff",DateTime.UtcNow.AddSeconds(-10).ToString("O")));
        var targets = new List<string>();
        using (var query = Sql("SELECT t.target_id FROM sync_inspection_targets t JOIN sync_inspection_jobs j ON j.job_id=t.job_id WHERE t.publisher_id=$publisher AND t.state IN ('queued','notified','running') AND j.state='active' ORDER BY t.target_id LIMIT 100", ("$publisher", publisher)))
        using (var reader = query.ExecuteReader()) while (reader.Read()) targets.Add(reader.GetString(0));
        foreach (var target in targets)
            Execute("UPDATE sync_inspection_targets SET state='notified',updated_utc=$now WHERE target_id=$id AND state='queued'", ("$id", target), ("$now", DateTime.UtcNow.ToString("O")));
        transaction.Commit(); return new(publisher,epoch,revision,targets);
    }

    public bool ReportInspection(SpaceInspectionReceipt receipt)
    {
        if (!device.Allows("index.write") || !device.Allows("index.read") || device.Kind != "gui") throw new UnauthorizedAccessException();
        if (receipt is null || !Guid.TryParseExact(receipt.TargetId,"N",out _) || receipt.Result is not ("running" or "completed" or "failed"))
            throw new ArgumentException("invalid_inspection_receipt");
        if (receipt.ErrorCode is not (null or "source_sync_failed" or "source_database_integrity_failed" or "pending_identity_adoptions" or "source_paths_unavailable" or "metadata_conflicts_pending") ||
            receipt.Result!="failed" && receipt.ErrorCode is not null) throw new ArgumentException("invalid_inspection_error");
        using var transaction = database.BeginTransaction();
        if (receipt.Epoch != SpaceEpoch()) throw new InvalidOperationException("publisher_epoch_reset_required");
        var state = Scalar("""
            SELECT t.state FROM sync_inspection_targets t JOIN sync_publishers p ON p.publisher_id=t.publisher_id
            WHERE t.target_id=$target AND p.publisher_id=$publisher AND p.incarnation_id=$device
                AND p.device_grant_id=$grant AND p.space_id=$space
                AND ((p.state='receiving' AND p.epoch=$epoch) OR (p.state='rebaseline' AND $result='running'))
            """, ("$target",receipt.TargetId),("$publisher",receipt.PublisherId),("$device",device.DeviceId),
            ("$grant",device.GrantId),("$space",device.SpaceId),("$epoch",receipt.Epoch),("$result",receipt.Result)) as string ?? throw new UnauthorizedAccessException();
        if (state is "completed" or "failed" or "revoked" or "cancelled" && receipt.Result == "running")
        { transaction.Commit(); return false; }
        Execute("INSERT INTO sync_source_presence(publisher_id,last_seen_utc) VALUES($publisher,$now) ON CONFLICT(publisher_id) DO UPDATE SET last_seen_utc=excluded.last_seen_utc",
            ("$publisher",receipt.PublisherId),("$now",DateTime.UtcNow.ToString("O")));
        if (state is "queued" or "notified" or "running")
            Execute("UPDATE sync_inspection_targets SET state=$state,updated_utc=$now,error_code=$error WHERE target_id=$target",
                ("$state",receipt.Result),("$now",DateTime.UtcNow.ToString("O")),("$error",receipt.Result == "failed" ? receipt.ErrorCode??"source_sync_failed" : null),("$target",receipt.TargetId));
        else if (state != receipt.Result && state != "cancelled") throw new InvalidOperationException("inspection_receipt_conflict");
        Execute("""
            UPDATE sync_inspection_jobs SET state=CASE WHEN NOT EXISTS(
                SELECT 1 FROM sync_inspection_targets t WHERE t.job_id=sync_inspection_jobs.job_id AND t.state<>'completed')
                THEN 'completed' ELSE 'partially_failed' END
            WHERE state='active' AND job_id=(SELECT job_id FROM sync_inspection_targets WHERE target_id=$target)
                AND NOT EXISTS(SELECT 1 FROM sync_inspection_targets t WHERE t.job_id=sync_inspection_jobs.job_id
                    AND t.state IN ('queued','notified','running'));
            """, ("$target",receipt.TargetId));
        transaction.Commit(); return state != "cancelled";
    }
}
