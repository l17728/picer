using Picer.Core.Identity;
using Picer.Core.Gateway;
using Picer.Core.Data;

namespace Picer.IndexService.Identity;

public sealed class SpaceSyncApi(AccountStore accounts) : IDisposable
{
    private readonly SemaphoreSlim _admission = new(4, 4);
    public async Task<IResult> WaitForNotificationAsync(HttpContext context, SpaceRuntimePool.Runtime runtime, AuthorizedDevice device, string accessToken)
    {
        if (!device.Allows("index.read") || device.Kind != "gui") return Results.StatusCode(403);
        try
        {
            var payload = await ReadAsync(context, context.RequestAborted, 4096);
            var request = DeviceAuthorization.ReadDocument<SpaceNotificationRequest>(payload);
            if (request.WaitSeconds is < 0 or > 25) return Results.BadRequest();
            var deadline = DateTime.UtcNow.AddSeconds(request.WaitSeconds);
            while (true)
            {
                await runtime.MutationGate.WaitAsync(context.RequestAborted);
                SpaceNotification notification;
                try
                {
                    notification = accounts.ExecuteAuthorizedWrite(device, accessToken, current =>
                    {
                        using var database = runtime.OpenDatabase();
                        return new SpaceSyncReceiver(database,current).ReadNotification(request);
                    });
                }
                finally { runtime.MutationGate.Release(); }
                if (notification.Epoch != request.Epoch || notification.Revision != request.After ||
                    notification.InspectionTargets.Count > 0 || DateTime.UtcNow >= deadline) return Results.Json(notification);
                await Task.Delay(TimeSpan.FromSeconds(2), context.RequestAborted);
            }
        }
        catch (UnauthorizedAccessException) { return Results.StatusCode(401); }
        catch (InvalidOperationException exception) when (exception.Message is "source_not_registered" or "space_fenced")
        { return Results.Json(new { error=exception.Message },statusCode:409); }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or System.Text.Json.JsonException)
        { return Results.BadRequest(new { error="invalid_notification_request" }); }
    }
    public async Task<IResult> ReadMetadataEditsAsync(HttpContext context, SpaceRuntimePool.Runtime runtime, AuthorizedDevice device,string accessToken)
    {
        if (!device.Allows("index.read")) return Results.StatusCode(403);
        if (!await _admission.WaitAsync(0, context.RequestAborted))
        { context.Response.Headers.RetryAfter = "2"; return Results.StatusCode(429); }
        try
        {
            using var database = runtime.OpenDatabase();
            return accounts.ExecuteAuthorizedWrite(device,accessToken,current=>Results.Json(new SpaceSyncReceiver(database,current).ReadMetadataEdits()));
        }
        catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
        catch (InvalidOperationException exception) when (exception.Message is "publisher_epoch_reset_required" or "space_fenced" or "metadata_edit_snapshot_too_large")
        { return Results.Json(new { error = exception.Message }, statusCode: 409); }
        finally { _admission.Release(); }
    }

    public async Task<IResult> ReadMetadataEditPageAsync(HttpContext context,SpaceRuntimePool.Runtime runtime,AuthorizedDevice device,string accessToken)
    {
        if (!await _admission.WaitAsync(0,context.RequestAborted))
        { context.Response.Headers.RetryAfter="2"; return Results.StatusCode(429); }
        try
        {
            if (!long.TryParse(context.Request.Query["after"],out var after) || after<0 ||
                !int.TryParse(context.Request.Query["pageSize"],out var size) || size is <1 or >500)
                return Results.BadRequest(new { error="invalid_metadata_cursor" });
            long? through=null;
            if (context.Request.Query.TryGetValue("through",out var cut))
            {
                if (!long.TryParse(cut,out var parsed) || parsed<0) return Results.BadRequest(new { error="invalid_metadata_cursor" });
                through=parsed;
            }
            using var database=runtime.OpenDatabase();
            return accounts.ExecuteAuthorizedWrite(device,accessToken,current=>Results.Json(new SpaceSyncReceiver(database,current)
                .ReadMetadataEditPage(after,context.Request.Query["operation"].ToString(),through,size,context.Request.Query["epoch"].ToString())));
        }
        catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
        catch (ArgumentException) { return Results.BadRequest(new { error="invalid_metadata_cursor" }); }
        catch (InvalidOperationException exception) when (exception.Message is "publisher_epoch_reset_required" or "space_fenced" or "metadata_change_baseline_required")
        { return Results.Json(new { error=exception.Message },statusCode:409); }
        finally { _admission.Release(); }
    }

    public async Task<IResult> ReadMetadataChangesAsync(HttpContext context, SpaceRuntimePool.Runtime runtime, AuthorizedDevice device,string accessToken)
    {
        if (!device.Allows("index.read")) return Results.StatusCode(403);
        if (!await _admission.WaitAsync(0, context.RequestAborted))
        { context.Response.Headers.RetryAfter = "2"; return Results.StatusCode(429); }
        try
        {
            using var database = runtime.OpenDatabase();
            var afterText = context.Request.Query["after"].ToString();
            if (afterText.Length > 0 && (!long.TryParse(afterText, out var parsed) || parsed < 0))
                return Results.BadRequest(new { error = "invalid_metadata_cursor" });
            var after = afterText.Length == 0 ? 0 : long.Parse(afterText, System.Globalization.CultureInfo.InvariantCulture);
            var sizeText = context.Request.Query["pageSize"].ToString();
            if (sizeText.Length > 0 && (!int.TryParse(sizeText, out var parsedSize) || parsedSize < 1 || parsedSize > 200))
                return Results.BadRequest(new { error = "invalid_metadata_page_size" });
            var pageSize = sizeText.Length == 0 ? 100 : int.Parse(sizeText, System.Globalization.CultureInfo.InvariantCulture);
            return accounts.ExecuteAuthorizedWrite(device,accessToken,current=>Results.Json(new SpaceSyncReceiver(database,current).ReadMetadataChanges(after,pageSize)));
        }
        catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
        catch (InvalidOperationException exception) when (exception.Message is "publisher_epoch_reset_required" or "space_fenced" or "metadata_change_baseline_required")
        { return Results.Json(new { error = exception.Message }, statusCode: 409); }
        finally { _admission.Release(); }
    }

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
                    "/api/sync/v2/media-reference" => Results.Json(receiver.ResolvePublishedMedia(DeviceAuthorization.ReadDocument<SpaceMediaReferenceRequest>(payload))),
                    "/api/sync/v2/rebaseline" => Results.Json(receiver.Rebaseline(DeviceAuthorization.ReadDocument<SpaceRebaselineRequest>(payload))),
                    "/api/sync/v2/batches" => Results.Json(receiver.Apply(DeviceAuthorization.ReadDocument<SpaceSyncBatch>(payload, 8 * 1024 * 1024))),
                    "/api/sync/v2/identity-offers" => Results.Json(receiver.ReadIdentityOffers(DeviceAuthorization.ReadDocument<SpaceIdentityOfferRequest>(payload))),
                    "/api/sync/v2/training-applications" => Results.Json(receiver.ReadTrainingApplications(DeviceAuthorization.ReadDocument<SpaceTrainingStateRequest>(payload))),
                    "/api/sync/v2/identity-adoptions" => Results.Json(receiver.AdoptIdentities(DeviceAuthorization.ReadDocument<SpaceIdentityAdoptionRequest>(payload))),
                    "/api/sync/v2/catalog-retirements/apply" => Results.Json(receiver.ApplyCatalogRetirements(DeviceAuthorization.ReadDocument<SpaceCatalogRetirementRequest>(payload))),
                    "/api/sync/v2/catalog-retirements/read" => Results.Json(receiver.ReadCatalogRetirements(DeviceAuthorization.ReadDocument<SpaceCatalogRetirementReadRequest>(payload))),
                    "/api/sync/v2/catalog-retirements/acknowledge" => Results.Json(receiver.AcknowledgeCatalogRetirements(DeviceAuthorization.ReadDocument<SpaceCatalogRetirementDeliveryRequest>(payload))),
                    "/api/sync/v2/catalog-retirements/completions" => Results.Json(receiver.ReadCatalogCompletions(DeviceAuthorization.ReadDocument<SpaceCatalogCompletionReadRequest>(payload))),
                    "/api/sync/v2/catalog-retirements/complete" => Results.Json(receiver.CompleteCatalogRetirements(DeviceAuthorization.ReadDocument<SpaceCatalogCompletionRequest>(payload))),
                    "/api/sync/v2/catalog-retirements/compact" => Results.Json(receiver.CompleteCatalogRetirements(DeviceAuthorization.ReadDocument<SpaceCatalogCompletionRequest>(payload),compact:true)),
                    "/api/sync/v2/inspections" => Results.Json(receiver.CreateInspection(DeviceAuthorization.ReadDocument<SpaceInspectionRequest>(payload))),
                    "/api/sync/v2/inspections/read" => ReadInspection(receiver,DeviceAuthorization.ReadDocument<SpaceInspectionJobRequest>(payload),current.SpaceId),
                    "/api/sync/v2/inspections/cancel" => Results.Json(receiver.CancelInspection(DeviceAuthorization.ReadDocument<SpaceInspectionJobRequest>(payload))),
                    "/api/sync/v2/inspections/receipt" => Results.Json(new { accepted=receiver.ReportInspection(DeviceAuthorization.ReadDocument<SpaceInspectionReceipt>(payload)) }),
                    "/api/sync/v2/video-ownership-reviews" => ReadVideoOwnershipReviews(database,current.SpaceId),
                    "/api/sync/v2/metadata-conflicts" => current.Allows("index.read") ? Results.Json(database.GetMetadataConflictSnapshot()) : Results.StatusCode(403),
                    "/api/sync/v2/catalog-conflicts" => current.Allows("index.read") ? Results.Json(database.GetCatalogRetirementReviews()) : Results.StatusCode(403),
                    "/api/sync/v2/catalog-conflicts/resolve" => current.Allows("index.read") ? ResolveCatalogConflict(database,DeviceAuthorization.ReadDocument<CatalogRetirementDecision>(payload)) : Results.StatusCode(403),
                    "/api/sync/v2/metadata-conflicts/resolve" => current.Allows("index.read") ? ResolveMetadataConflict(database,DeviceAuthorization.ReadDocument<MetadataConflictResolution>(payload)) : Results.StatusCode(403),
                    "/api/sync/v2/archived-operations" => Results.Json(receiver.ReadArchivedOperations(DeviceAuthorization.ReadDocument<SpaceArchivedOperationRequest>(payload))),
                    "/api/sync/v2/conflict-resolutions" => Results.Json(receiver.ReadConflictResolutions(DeviceAuthorization.ReadDocument<SpaceArchivedOperationRequest>(payload))),
                    "/api/sync/v2/metadata-operation-states" => Results.Json(receiver.ReadMetadataOperationStates(DeviceAuthorization.ReadDocument<SpaceArchivedOperationRequest>(payload))),
                    "/api/sync/v2/video-ownership-reviews/resolve" => ResolveVideoOwnershipReview(database,DeviceAuthorization.ReadDocument<VideoOwnershipResolution>(payload)),
                    "/api/sync/v2/source-bindings" => Results.Json(new { bound = SpaceSourceRouting.Bind(database, accounts, current,
                        DeviceAuthorization.ReadDocument<SpaceSourceBindingRequest>(payload)) }),
                    _ => Results.NotFound()
                };
            }); }
            finally { runtime.MutationGate.Release(); }
        }
        catch (InvalidOperationException exception) when (exception.Message is "space_epoch_reset_required" or "publisher_epoch_reset_required" or
            "sync_sequence_gap" or "sync_sequence_conflict" or "space_fenced" or "identity_adoption_conflict" or "too_many_active_inspections" or "inspection_receipt_conflict" or "video_ownership_review_changed" or "video_ownership_review_missing" or
            "metadata_conflict_changed" or "metadata_conflict_missing" or "metadata_conflict_resolution_changed" or "metadata_conflict_entity_unavailable" or
            "video_face_ownership_review_changed" or "video_face_review_geometry_changed" or "video_face_missing" or "video_content_changed" or "video_source_ownership_ambiguous")
        { return Results.Json(new { error = exception.Message }, statusCode: 409); }
        catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or System.Text.Json.JsonException)
        { return Results.BadRequest(new { error = "invalid_sync_request" }); }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        { return Results.StatusCode(408); }
        finally { _admission.Release(); }
    }

    private IResult ReadVideoOwnershipReviews(PicerDatabase database,string spaceId)
    {
        var grants=accounts.ReadSpaceSourceGrants(spaceId);
        var rows=database.GetVideoOwnershipReviews().Select(review=>review with
        {
            Candidates=review.Candidates.Select(candidate=>
            {
                using var source=database.Command("SELECT p.device_grant_id FROM sync_publishers p JOIN sync_source_bindings b ON b.publisher_id=p.publisher_id JOIN videos v ON v.source_id=b.source_id WHERE v.id=$id LIMIT 1");
                source.Parameters.AddWithValue("$id",candidate.VideoId);
                return source.ExecuteScalar() is string grant && grants.TryGetValue(grant,out var info)
                    ? candidate with { SourceName=info.Name } : candidate;
            }).ToArray()
        }).ToArray();
        return Results.Json(rows);
    }

    private static IResult ResolveVideoOwnershipReview(PicerDatabase database,VideoOwnershipResolution request)
    {
        database.ResolveVideoOwnershipReview(request,authoritative:true);
        return Results.Json(new { resolved=true });
    }

    private static IResult ResolveCatalogConflict(PicerDatabase database,CatalogRetirementDecision request)
    {
        database.ResolveCatalogRetirementReview(request,new SpaceUploadScope(database.GetSources().Select(source=>source.Id).ToArray()));
        return Results.Json(new { resolved=true });
    }

    private static IResult ResolveMetadataConflict(PicerDatabase database,MetadataConflictResolution request)
    {
        database.ResolveMetadataEditConflict(request.OperationId,request.UseProposed,request.ExpectedVersion,request.ExpectedOperationId,
            authoritative:true,expectedEpoch:request.Epoch);
        return Results.Json(new { resolved=true });
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
            result = Results.Json(database.AcceptMetadataEditsInTransaction(edits, transaction));
        }
        else
        {
            var edits = DeviceAuthorization.ReadDocument<GatewayFaceReviewDecision[]>(payload, 8 * 1024 * 1024);
            if (edits.Length > 500) throw new ArgumentException("face_review_batch_too_large");
            result = Results.Json(database.MergeFaceReviewDecisionsInTransaction(edits, transaction));
        }
        transaction.Commit(); return result;
    }

    private IResult ReadInspection(SpaceSyncReceiver receiver, SpaceInspectionJobRequest request, string spaceId)
    {
        var grants = accounts.ReadSpaceSourceGrants(spaceId);
        return Results.Json(receiver.ReadInspection(request,
            grant => grants.TryGetValue(grant,out var info) && info.Active,
            grant => grants.TryGetValue(grant,out var info) ? info.Name : null));
    }

    private static async Task<byte[]> ReadAsync(HttpContext context, CancellationToken cancellationToken, int maximum = 8 * 1024 * 1024)
    {
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
