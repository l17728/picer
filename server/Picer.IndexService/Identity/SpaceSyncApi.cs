using Picer.Core.Identity;
using Picer.Core.Gateway;
using Picer.Core.Data;

namespace Picer.IndexService.Identity;

public sealed class SpaceSyncApi(AccountStore accounts) : IDisposable
{
    private readonly SemaphoreSlim _admission = new(4, 4);
    public async Task<IResult> InvokeAsync(HttpContext context, string path, SpaceRuntimePool.Runtime runtime, AuthorizedDevice device, string accessToken)
    {
        if (!device.Allows("index.write")) return Results.StatusCode(403);
        if (!await _admission.WaitAsync(0, context.RequestAborted))
        { context.Response.Headers.RetryAfter = "2"; return Results.StatusCode(429); }
        try
        {
            using var intake = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            intake.CancelAfter(TimeSpan.FromSeconds(30));
            var payload = await ReadAsync(context, intake.Token);
            await runtime.MutationGate.WaitAsync(intake.Token);
            try { return accounts.ExecuteAuthorizedWrite(device, accessToken, current =>
            {
                using var database = runtime.OpenDatabase();
                if (path is "/api/sync/v1/metadata-edits" or "/api/sync/v1/face-review-decisions")
                    return ApplyMirrorEdit(context, path, payload, database);
                var receiver = new SpaceSyncReceiver(database, current);
                return path switch
                {
                    "/api/sync/v2/publishers" => (IResult)Results.Json(receiver.Register(DeviceAuthorization.ReadDocument<SpacePublisherRequest>(payload))),
                    "/api/sync/v2/rebaseline" => Results.Json(receiver.Rebaseline(DeviceAuthorization.ReadDocument<SpaceRebaselineRequest>(payload))),
                    "/api/sync/v2/batches" => Results.Json(receiver.Apply(DeviceAuthorization.ReadDocument<SpaceSyncBatch>(payload, 8 * 1024 * 1024))),
                    "/api/sync/v2/source-bindings" => Results.Json(new { bound = SpaceSourceRouting.Bind(database, accounts, current,
                        DeviceAuthorization.ReadDocument<SpaceSourceBindingRequest>(payload)) }),
                    _ => Results.NotFound()
                };
            }); }
            finally { runtime.MutationGate.Release(); }
        }
        catch (InvalidOperationException exception) when (exception.Message is "space_epoch_reset_required" or "publisher_epoch_reset_required" or
            "sync_sequence_gap" or "sync_sequence_conflict" or "space_fenced")
        { return Results.Json(new { error = exception.Message }, statusCode: 409); }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or System.Text.Json.JsonException)
        { return Results.BadRequest(new { error = "invalid_sync_request" }); }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        { return Results.StatusCode(408); }
        finally { _admission.Release(); }
    }

    private static IResult ApplyMirrorEdit(HttpContext context, string path, byte[] payload, PicerDatabase database)
    {
        using var transaction = database.BeginTransaction();
        using var state = database.Command("SELECT epoch FROM mirror_revision WHERE id=1 AND EXISTS(SELECT 1 FROM sync_space_state WHERE id=1 AND state='active')");
        var epoch = state.ExecuteScalar() as string;
        if (epoch is null || context.Request.Headers["X-Picer-Mirror-Epoch"].ToString() != epoch)
            return Results.Json(new { error = "remote_baseline_required" }, statusCode: 409);
        IResult result;
        if (path == "/api/sync/v1/metadata-edits")
        {
            var edits = DeviceAuthorization.ReadDocument<MetadataEdit[]>(payload, 8 * 1024 * 1024);
            if (edits.Length > 500) throw new ArgumentException("metadata_batch_too_large");
            result = Results.Json(database.MergeMetadataEditsInTransaction(edits, transaction));
        }
        else
        {
            var edits = DeviceAuthorization.ReadDocument<GatewayFaceReviewDecision[]>(payload, 8 * 1024 * 1024);
            if (edits.Length > 500) throw new ArgumentException("face_review_batch_too_large");
            result = Results.Json(database.MergeFaceReviewDecisionsInTransaction(edits, transaction));
        }
        transaction.Commit(); return result;
    }

    private static async Task<byte[]> ReadAsync(HttpContext context, CancellationToken cancellationToken)
    {
        const int maximum = 8 * 1024 * 1024;
        if (context.Request.ContentLength > maximum) throw new InvalidDataException("sync_request_too_large");
        using var memory = new MemoryStream();
        var block = new byte[81920];
        int read;
        while ((read = await context.Request.Body.ReadAsync(block, cancellationToken)) > 0)
        {
            if (memory.Length + read > maximum) throw new InvalidDataException("sync_request_too_large");
            memory.Write(block, 0, read);
        }
        return memory.ToArray();
    }
    public void Dispose() => _admission.Dispose();
}
