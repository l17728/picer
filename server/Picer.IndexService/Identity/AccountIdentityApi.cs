using System.Security.Cryptography;
using System.Text.Json;
using Picer.Core.Identity;

namespace Picer.IndexService.Identity;

public sealed record EnrollmentRedemption(string RequestId, string RedemptionCode, string AttemptId);
public sealed record SessionRefresh(string RefreshToken, string AttemptId);

public static class AccountIdentityApi
{
    public static void Map(WebApplication app, AccountStore store, string origin, string spkiPin, ECDsa signingKey)
    {
        origin = DeviceAuthorization.NormalizeOrigin(origin);
        app.MapGet("/identity/service", () => Results.Json(new
        {
            version = 2, serviceId = store.ServiceId, origin, spkiPin,
            signingKey = DeviceAuthorization.ExportPublicKey(signingKey),
            enrollmentEndpoint = origin + "/identity/enrollments",
            accountEndpoint = origin + "/account/login"
        }));
        app.MapPost("/identity/enrollments", async (HttpContext context) =>
        {
            var request = await Read<DeviceEnrollmentRequest>(context);
            var proof = Proof(context, store, origin);
            if (request.PublicKey != proof.PublicKey) throw new UnauthorizedAccessException("key_mismatch");
            var id = store.CreateEnrollment(request);
            return Results.Json(new { requestId = id, approvalUrl = origin + "/account/approve?request=" + id, expiresIn = 900 });
        }).RequireRateLimiting("enrollment-work");
        app.MapPost("/identity/redeem", async (HttpContext context) =>
        {
            var request = await Read<EnrollmentRedemption>(context);
            var proof = Proof(context, store, origin);
            return Results.Json(store.Redeem(request.RequestId, request.RedemptionCode, proof.PublicKey, request.AttemptId), DeviceAuthorization.Json);
        }).RequireRateLimiting("account-work");
        app.MapPost("/identity/refresh", async (HttpContext context) =>
        {
            var request = await Read<SessionRefresh>(context);
            var proof = Proof(context, store, origin);
            return Results.Json(store.Refresh(request.RefreshToken, proof.PublicKey, request.AttemptId), DeviceAuthorization.Json);
        }).RequireRateLimiting("account-work");
        app.MapPost("/identity/activate", (HttpContext context) =>
        {
            var (device, _) = Authenticate(context, store, origin);
            return Results.Json(new { device.DeviceId, device.SpaceId, device.Scope });
        }).RequireRateLimiting("account-work");
    }

    internal static (AuthorizedDevice Device, string AccessToken) Authenticate(HttpContext context, AccountStore store, string origin, string? space = null)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("DPoP ", StringComparison.Ordinal) || authorization.Length > 134)
            throw new UnauthorizedAccessException("device_authorization_required");
        var access = authorization[5..];
        var proof = Proof(context, store, origin, access);
        return (store.Authorize(access, proof.PublicKey, space) ?? throw new UnauthorizedAccessException("device_authorization_invalid"), access);
    }

    private static DeviceProof.Validated Proof(HttpContext context, AccountStore store, string origin, string? access = null)
    {
        var proof = DeviceProof.Validate(context.Request.Headers["DPoP"].ToString(), context.Request.Method,
            new Uri(origin + context.Request.PathBase + context.Request.Path), DateTimeOffset.UtcNow, access);
        store.AcceptProof(proof);
        return proof;
    }

    private static async Task<T> Read<T>(HttpContext context)
    {
        if (context.Request.ContentLength > DeviceAuthorization.MaximumFileBytes) throw new InvalidDataException("request_too_large");
        using var buffer = new MemoryStream();
        var block = new byte[4096];
        int read;
        while ((read = await context.Request.Body.ReadAsync(block, context.RequestAborted)) != 0)
        {
            if (buffer.Length + read > DeviceAuthorization.MaximumFileBytes) throw new InvalidDataException("request_too_large");
            buffer.Write(block, 0, read);
        }
        return DeviceAuthorization.ReadDocument<T>(buffer.ToArray());
    }
}
