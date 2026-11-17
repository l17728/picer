using Picer.Core.Gateway;
using Picer.Core.Identity;

namespace Picer.IndexService.Identity;

public sealed partial class SpaceSyncApi
{
    public async Task<IResult> ReadDescriptionsAsync(HttpContext context, SpaceRuntimePool.Runtime runtime,
        AuthorizedDevice device, string accessToken)
    {
        if (device.Kind != "gui" || !device.Allows("index.read")) return Results.StatusCode(403);
        if (!await _admission.WaitAsync(0, context.RequestAborted)) return Results.StatusCode(429);
        try
        {
            var request = DeviceAuthorization.ReadDocument<DescriptionDeltaRequest>(await ReadAsync(context, context.RequestAborted, 8192));
            await runtime.MutationGate.WaitAsync(context.RequestAborted);
            try
            {
                if (runtime.Purging) return Results.Json(new { error = "description_delta_baseline_required" }, statusCode: 409);
                return accounts.ExecuteAuthorizedWrite(device, accessToken, current =>
                {
                    var scope = DeviceAuthorization.Digest(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        current.SpaceId, current.GrantId, current.AuthEpoch, current.DataEpoch,
                        current.SecurityEpoch, current.MembershipVersion, current.Scope, Domain = "descriptions-v1"
                    }));
                    using var database = runtime.OpenDatabase();
                    return Results.Json(database.ReadDescriptionDelta(request, scope));
                });
            }
            finally { runtime.MutationGate.Release(); }
        }
        catch (UnauthorizedAccessException) { return Results.StatusCode(403); }
        catch (InvalidOperationException exception) when (exception.Message == "description_delta_baseline_required")
        { return Results.Json(new { error = "description_delta_baseline_required" }, statusCode: 409); }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or System.Text.Json.JsonException)
        { return Results.BadRequest(new { error = "invalid_description_cursor" }); }
        finally { _admission.Release(); }
    }
}
