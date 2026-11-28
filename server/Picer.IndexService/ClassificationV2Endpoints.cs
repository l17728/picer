using System.Text;
using Picer.Core.Classification;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Models;

namespace Picer.IndexService;

internal static class ClassificationV2Endpoints
{
    internal static void Map(WebApplication app, PicerDatabase database, AgentConnectionRegistry agents, bool isolatedSpace,
        ClassificationAuthorization? authorization)
    {
        const string root = "/api/service/v1/classification/v2";
        var group = app.MapGroup(root);
        var browseCache = new ClassificationBrowseCache(database);
        app.Lifetime.ApplicationStopped.Register(browseCache.Dispose);
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "classification_request_invalid" }); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
            catch (InvalidDataException) { return Results.BadRequest(new { error = "classification_data_invalid" }); }
            catch (InvalidOperationException exception) when (exception.Message.StartsWith("classification_", StringComparison.Ordinal))
            { return Results.Conflict(new { error = exception.Message }); }
        });
        group.MapGet("/capabilities", () => Results.Ok(new { version = 2, durableJobs = true, taxonomy = "family-taxonomy-v1" }));
        group.MapGet("/taxonomy", () => Read(repository => repository.Taxonomy()));
        group.MapPost("/taxonomy/edit", (ClassificationTaxonomyEdit edit, HttpContext context) =>
            Write(context, "taxonomy.manage", repository => repository.EditTaxonomy(edit)));
        group.MapGet("/replica", (string? knownSnapshot, CancellationToken token) =>
        {
            var replica = browseCache.ExportReplica(token);
            return replica.Snapshot == knownSnapshot ? Results.NoContent() : Results.Ok(replica);
        });
        group.MapGet("/configuration", () => Read(repository => repository.Configuration()));
        group.MapPost("/configuration", (ClassificationConfiguration configuration, HttpContext context) =>
            Write(context, "index.write", repository => { repository.Configure(configuration); return configuration; }));
        group.MapPost("/browse", (ClassificationBrowseRequest request, CancellationToken token) =>
            Results.Ok(browseCache.Browse(request, token)));
        group.MapPost("/incremental-plans", (PlanRequest request) =>
        {
            using var db = database.OpenIndependentConnection();
            var repository = new ClassificationRepository(db);
            return Results.Ok(ClassificationPlanPreview.From(repository.Plan(request.RequestId, repository.Configuration() ?? throw new InvalidOperationException("classification_configuration_required"), request.RetryFailures)));
        });
        group.MapPost("/jobs", (JobRequest request, HttpContext context) =>
        {
            using var db = database.OpenIndependentConnection();
            return Results.Ok(new ClassificationRepository(db).Start(request.PlanId, request.RequestId, Actor(context)));
        });
        group.MapGet("/jobs/latest", () => Read(repository => repository.LatestJob()));
        group.MapGet("/jobs/{id}", (string id) => Read(repository => repository.Job(id)));
        group.MapPost("/jobs/{id}/{operation}", (string id, string operation, HttpContext context) =>
        {
            using var db = database.OpenIndependentConnection();
            var repository = new ClassificationRepository(db);
            repository.Control(id, operation, Actor(context));
            return Results.Ok(repository.Job(id));
        });
        group.MapGet("/proposals", () => Read(repository => repository.Proposals()));
        group.MapPost("/proposals/accept-and-match", (ClassificationProposalReview review, HttpContext context) =>
        {
            var actor = Actor(context);
            return Write(context, "taxonomy.manage", repository => repository.AcceptAndMatch(review, actor));
        });
        group.MapGet("/clusters/latest", () => Read(repository => repository.LatestCluster()));
        group.MapPost("/clusters", (ClassificationClusterRequest request, HttpContext context) =>
        {
            using var db = database.OpenIndependentConnection();
            var task = new ClassificationRepository(db).PlanCluster(request, Actor(context));
            return Results.Ok(new { task.Id, task.State, task.CandidateCount, Representatives = task.Representatives.Count });
        });
        group.MapGet("/epoch", () => Read(repository => repository.Epoch));
        group.MapPost("/decisions", (ClassificationDecision decision, HttpContext context) =>
            Write(context, "index.write", repository => repository.Decide(decision)));
        group.MapPost("/proposals/review", (ClassificationProposalReview review, HttpContext context) =>
            Write(context, "taxonomy.manage", repository => repository.Review(review)));

        var model = new ClassificationModel(async (image, token) =>
        {
            string? sourceAgent;
            using (var db = database.OpenReadOnlyConnection()) sourceAgent = Identity.SpaceSourceRouting.Resolve(db, $"/api/gateway/v1/assets/images/{image.Id}");
            if (isolatedSpace && sourceAgent is null) throw new InvalidDataException("classification_source_unbound");
            var source = new MediaWire.SourceFingerprint(image.FileSize, image.ModifiedUtc, image.ContentSha256 ?? image.Sha256);
            await using var stream = await agents.OpenMediaAsync(sourceAgent, IndexApiHandlers.NormalizeAgentPath(image.Path), 1280, token, sourceFingerprint: source);
            if (stream is null || stream.Length is <= 0 or > 8 * 1024 * 1024) throw new InvalidDataException("classification_media_unavailable");
            using var output = new MemoryStream();
            await stream.CopyToAsync(output, token);
            return output.ToArray();
        }, async (configuration, payload, token) =>
        {
            using var output = new MemoryStream();
            await agents.StreamAiAsync(configuration.ModelAgentId, payload, async chunk =>
            {
                if (output.Length + chunk.Length > ClassificationModel.MaximumTransportBytes) throw new InvalidDataException("classification_transport_too_large");
                await output.WriteAsync(chunk, token);
            }, token);
            return Encoding.UTF8.GetString(output.ToArray());
        }, actor =>
        {
            if (isolatedSpace && (authorization is null || !authorization.IsCurrent(actor)))
                throw new UnauthorizedAccessException("classification_authorization_revoked");
        });
        var worker = new ClassificationWorker(database.OpenIndependentConnection, model,
            isolatedSpace ? authorization ?? new(_ => false, (_, _) => throw new UnauthorizedAccessException()) : null);
        app.Lifetime.ApplicationStarted.Register(() => _ = RunAsync());
        async Task RunAsync()
        {
            var stop = app.Lifetime.ApplicationStopping;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    if (agents.OnlineCount > 0 && await worker.ProcessOneAsync(stop)) continue;
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    Picer.Core.Logging.AppLogger.Warn($"Classification worker deferred; type={exception.GetType().Name}");
                }
                try { await Task.Delay(TimeSpan.FromSeconds(2), stop); }
                catch (OperationCanceledException) { break; }
            }
        }
        IResult Read<T>(Func<ClassificationRepository, T> action)
        {
            using var db = database.OpenReadOnlyConnection();
            return Results.Ok(action(new ClassificationRepository(db)));
        }
        IResult Write<T>(HttpContext context, string scope, Func<ClassificationRepository, T> action)
        {
            T result = default!;
            void Apply()
            {
                context.RequestAborted.ThrowIfCancellationRequested();
                using var db = database.OpenIndependentConnection();
                result = action(new ClassificationRepository(db));
            }
            if (!isolatedSpace) Apply();
            else
            {
                var actor = context.Request.Headers["X-Picer-Classification-Actor"].ToString();
                if (actor.Length > 16384 || authorization?.CommitScoped is null) throw new UnauthorizedAccessException();
                authorization.CommitScoped(actor, scope, Apply);
            }
            return Results.Ok(result);
        }
        string? Actor(HttpContext context)
        {
            if (!isolatedSpace) return null;
            var actor = context.Request.Headers["X-Picer-Classification-Actor"].ToString();
            if (actor.Length > 16384 || authorization is null || !authorization.IsCurrent(actor))
                throw new UnauthorizedAccessException("classification_authorization_required");
            return actor;
        }
    }

    private sealed record PlanRequest(string RequestId, bool RetryFailures = false);
    private sealed record JobRequest(string PlanId, string RequestId);
}
