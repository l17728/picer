using Picer.Core.Data;

namespace Picer.Core.Classification;

public sealed class ClassificationWorker(Func<PicerDatabase> openDatabase, IClassificationModel model, ClassificationAuthorization? authorization = null)
{
    public async Task<bool> ProcessOneAsync(CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        ClassificationLease? lease;
        ClassificationConfiguration config;
        ClassificationContext? context;
        ClassificationTaxonomySnapshot taxonomy;
        using (var db = openDatabase())
        {
            var repository = new ClassificationRepository(db);
            if (repository.Configuration() is not { Enabled: true } || !repository.HasRunnableWork()) return false;
            lease = repository.Claim();
            if (lease is null) return await ProcessClusterAsync(cancellationToken).ConfigureAwait(false);
            if (authorization is not null && !authorization.IsCurrent(lease.Actor))
            { repository.BlockAuthorization(lease, modelCalled: false); return true; }
            config = repository.JobConfiguration(lease.JobId);
            context = repository.Contexts(config, lease.ImageId).SingleOrDefault();
            taxonomy = repository.Taxonomy();
        }
        try
        {
            if (context is null || ClassificationInputFingerprint.Compute(context.Input) != lease.Fingerprint)
            {
                using var staleDb = openDatabase();
                new ClassificationRepository(staleDb).Supersede(lease);
                return true;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var result = await RunAuthorized(lease.Actor, token => model.ClassifyAsync(context with { Actor = lease.Actor, RetryIncompleteOutput = lease.RetryIncompleteOutput }, taxonomy, config, token), timeout.Token).ConfigureAwait(false);
            Commit(lease.Actor, () => { using var db = openDatabase(); new ClassificationRepository(db).Publish(lease, result); });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A restart can reclaim the durable lease after expiry. No success is recorded.
            throw;
        }
        catch (Exception exception)
        {
            if (authorization is not null && !authorization.IsCurrent(lease.Actor))
            { using var revokedDb = openDatabase(); new ClassificationRepository(revokedDb).BlockAuthorization(lease); return true; }
            var retry = IsRetryableFailure(exception);
            var code = IsTransientMediaFailure(exception) ? "classification_media_temporarily_unavailable"
                : exception is System.Text.Json.JsonException ? "classification_json_invalid"
                : exception is InvalidDataException && exception.Message.StartsWith("classification_", StringComparison.Ordinal)
                ? exception.Message : retry ? "classification_transport_failed" : "classification_failed";
            Picer.Core.Logging.AppLogger.Warn($"Classification item failed; image={lease.ImageId}; job={lease.JobId}; code={code}; type={exception.GetType().Name}; elapsedMs={started.ElapsedMilliseconds}");
            using var db = openDatabase();
            new ClassificationRepository(db).Fail(lease, code, retry);
        }
        return true;
    }

    internal static bool IsRetryableFailure(Exception exception) => exception is HttpRequestException or TimeoutException or OperationCanceledException
        or System.Text.Json.JsonException
        || IsTransientMediaFailure(exception)
        || exception is InvalidDataException invalid && invalid.Message is "classification_output_invalid"
            or "classification_output_incomplete" or "classification_evidence_invalid" or "classification_proposal_invalid";

    private static bool IsTransientMediaFailure(Exception exception) => exception is IOException io && io.Message is
        "media_agent_offline" or "media_agent_disconnected" or "media_channel_busy" or "media_pool_invalidated"
        or "media_service_stopping" or "media_reader_capacity";

    private async Task<bool> ProcessClusterAsync(CancellationToken cancellationToken)
    {
        if (model is not IClassificationClusterModel clusterModel) return false;
        (ClassificationClusterTask Task, string Lease)? claimed;
        ClassificationTaxonomySnapshot taxonomy;
        using (var db = openDatabase())
        {
            var repository = new ClassificationRepository(db);
            claimed = repository.ClaimCluster(); taxonomy = repository.Taxonomy();
        }
        if (claimed is not { } work) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var result = await RunAuthorized(work.Task.Actor, token => clusterModel.ClusterAsync(work.Task, taxonomy, token), timeout.Token).ConfigureAwait(false);
            Commit(work.Task.Actor, () => { using var db = openDatabase(); new ClassificationRepository(db).CompleteCluster(work.Task, work.Lease, result); });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            using var db = openDatabase();
            new ClassificationRepository(db).FailCluster(work.Task.Id, work.Lease);
        }
        return true;
    }
    private void Commit(string? actor, Action action)
    { if (authorization is null) action(); else authorization.Commit(actor, action); }
    private async Task<T> RunAuthorized<T>(string? actor, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        if (authorization is null) return await action(cancellationToken).ConfigureAwait(false);
        if (!authorization.IsCurrent(actor)) throw new UnauthorizedAccessException("classification_authorization_revoked");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var observer = Observe();
        try { return await action(linked.Token).ConfigureAwait(false); }
        finally { await linked.CancelAsync().ConfigureAwait(false); await observer.ConfigureAwait(false); }
        async Task Observe()
        {
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    await Task.Delay(2000, linked.Token).ConfigureAwait(false);
                    if (!authorization.IsCurrent(actor)) { await linked.CancelAsync().ConfigureAwait(false); return; }
                }
            }
            catch (OperationCanceledException) { }
        }
    }
}
