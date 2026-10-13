using Picer.Core.Gateway;
using Picer.Core.Models;

namespace Picer.IndexService.Identity;

public sealed class SpacePurgeApi(AccountStore accounts, RestoreGuard guard, SpaceReadSnapshots snapshots)
{
    public static (string Kind, long Id)? Target(string method, string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (method != "POST" || parts.Length != 6 || parts[0] != "api" || parts[1] != "gateway" || parts[2] != "v1" ||
            !long.TryParse(parts[4], out var id) || id <= 0) return null;
        return (parts[3], parts[5]) switch { ("images", "purge-missing") => ("image", id), ("videos", "purge") => ("video", id), _ => null };
    }

    public async Task<IResult> InvokeAsync(HttpContext context, SpaceRuntimePool.Runtime runtime,
        AuthorizedDevice device, string accessToken, string kind, long id)
    {
        if (!device.Allows("index.purge")) return Results.StatusCode(403);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await runtime.MutationGate.WaitAsync(deadline.Token);
        try
        {
            using var database = runtime.OpenDatabase();
            using var epoch = database.Command("SELECT epoch FROM mirror_revision WHERE id=1");
            if (context.Request.Headers["X-Picer-Mirror-Epoch"].ToString() != epoch.ExecuteScalar() as string)
                return Results.Json(new { error = "remote_baseline_required" }, statusCode: 409);
            if (kind == "image" && database.GetImage(id) is { Status: not ImageStatus.Missing }) return Results.Conflict(new { error = "image_not_missing" });
            runtime.Purging = true;
            runtime.CancelDataReaders();
            snapshots.Invalidate(device.SpaceId);
            var agent = SpaceSourceRouting.Resolve(database, $"/api/gateway/v1/assets/{(kind == "image" ? "images" : "videos")}/{id}");
            await runtime.PrepareIndexPurgeAsync(kind, id, agent ?? "unbound-source", deadline.Token);
            return accounts.ExecuteAuthorizedWrite(device, accessToken, current =>
            {
                if (!current.Allows("index.purge")) throw new UnauthorizedAccessException();
                new SpacePurgeCoordinator(database, guard, current.SpaceId).Purge(kind, id);
                return Results.Json(new PipeFlagResponse(true));
            });
        }
        finally { runtime.Purging = false; runtime.MutationGate.Release(); }
    }
}
