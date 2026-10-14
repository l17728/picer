using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Picer.Core.Identity;
using Picer.Core.Logging;

namespace Picer.IndexService.Identity;

public sealed record AccountServiceInstance(WebApplication App, AccountStore Accounts, X509Certificate2 Certificate);

public static class AccountServiceHost
{
    public static AccountServiceInstance Build(string root, string publicOrigin, int port = 8445,
        IPAddress? bindAddress = null, bool allowRegistration = true)
    {
        publicOrigin = DeviceAuthorization.NormalizeOrigin(publicOrigin);
        Directory.CreateDirectory(root);
        AppLogger.Initialize(Path.Combine(root, "logs"));
        // v2 storage deliberately has a separate root: old executables cannot open it as a v1 service database.
        var accounts = new AccountStore(Path.Combine(root, "accounts-v2"));
        var guardRoot = Environment.GetEnvironmentVariable("PICER_RESTORE_GUARD_ROOT")
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(root))!, "picer-restore-guard", accounts.ServiceId);
        var guard = new RestoreGuard(guardRoot, accounts.ServiceId, allowCreate: !accounts.HasSpaces);
        accounts.InitializeSpaceGuard = guard.InitializeNewSpace;
        var certificate = AccountHttpsCertificate.Load(root, publicOrigin);
        var signingKey = IdentitySigningKey.LoadOrCreate(accounts.Root, accounts.ServiceId);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 16 * 1024 * 1024;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            options.Listen(bindAddress ?? IPAddress.Loopback, port, listener => listener.UseHttps(certificate));
        });
        AccountWebPortal.AddServices(builder, accounts);
        builder.Services.AddResponseCompression(options => { options.EnableForHttps = true; options.MimeTypes = ["application/json"]; });
        builder.Services.AddSingleton(guard);
        builder.Services.AddSingleton(_ => new AccountRuntimeLease(accounts.Root));
        builder.Services.AddSingleton<SpaceRuntimePool>();
        builder.Services.AddSingleton<SpaceSyncApi>();
        builder.Services.AddSingleton<SpacePurgeApi>();
        builder.Services.AddSingleton<SpaceReadSnapshots>();
        builder.Services.AddSingleton<SpaceWebSocketRelay>();
        builder.Services.AddSingleton<SpaceRequestAdmission>();
        var app = builder.Build();
        _ = app.Services.GetRequiredService<AccountRuntimeLease>();
        var pool = app.Services.GetRequiredService<SpaceRuntimePool>();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.UseResponseCompression();
        app.Use(async (context, next) =>
        {
            if (!context.Request.IsHttps || !string.Equals(context.Request.Scheme + "://" + context.Request.Host, publicOrigin, StringComparison.OrdinalIgnoreCase))
            { context.Response.StatusCode = 400; return; }
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers.CacheControl = "no-store";
            try { await next(); }
            catch (UnauthorizedAccessException)
            {
                if (context.Response.HasStarted) { context.Abort(); return; }
                context.Response.StatusCode = 401;
                context.Response.Headers.WWWAuthenticate = "DPoP";
                await context.Response.WriteAsJsonAsync(new { error = "authorization_required" });
            }
            catch (SpaceCapacityException)
            {
                if (context.Response.HasStarted) { context.Abort(); return; }
                context.Response.StatusCode = 503;
                context.Response.Headers.RetryAfter = "2";
                await context.Response.WriteAsJsonAsync(new { error = "space_capacity_busy" });
            }
            catch (InvalidOperationException exception) when (exception.Message is "read_snapshot_required" or "read_snapshot_expired")
            {
                if (context.Response.HasStarted) { context.Abort(); return; }
                context.Response.StatusCode = 409;
                await context.Response.WriteAsJsonAsync(new { error = exception.Message });
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                if (context.Response.HasStarted) { context.Abort(); return; }
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = "invalid_request" });
            }
        });
        AccountWebPortal.Map(app, accounts, publicOrigin, Convert.ToBase64String(ServiceSecurity.ComputeSpkiPin(certificate)), signingKey, allowRegistration);
        AccountIdentityApi.Map(app, accounts, publicOrigin, Convert.ToBase64String(ServiceSecurity.ComputeSpkiPin(certificate)), signingKey);
        app.MapGet("/", () => Results.Redirect("/account"));
        app.MapGet("/health", () => Results.Json(new { status = "ok", protocol = 2 }));
        app.MapGet("/version", IndexServiceHost.Version);
        app.MapMethods("/spaces/{spaceId}/{**path}", ["GET", "HEAD", "POST", "PUT", "DELETE"], async (HttpContext context, string spaceId, string? path) =>
        {
            if (!Guid.TryParseExact(spaceId, "N", out _)) return Results.NotFound();
            var (device, accessToken) = AccountIdentityApi.Authenticate(context, accounts, publicOrigin, spaceId);
            var relative = "/" + (path ?? "");
            using var admission = app.Services.GetRequiredService<SpaceRequestAdmission>().TryEnter(device, relative);
            if (admission is null) { context.Response.Headers.RetryAfter = "2"; return Results.StatusCode(429); }
            const string snapshotPrefix = "/api/sync/v2/read-snapshots/";
            if (context.Request.Method == "DELETE" && relative.StartsWith(snapshotPrefix, StringComparison.Ordinal))
            {
                if (!device.Allows("index.read")) return Results.StatusCode(403);
                app.Services.GetRequiredService<SpaceReadSnapshots>().Release(device, relative[snapshotPrefix.Length..]);
                return Results.NoContent();
            }
            if (SpacePurgeApi.Target(context.Request.Method, relative) is { } purge)
            {
                if (!device.Allows("index.purge")) return Results.StatusCode(403);
                await using var purgeLease = await pool.AcquireAsync(spaceId, context.RequestAborted);
                return await app.Services.GetRequiredService<SpacePurgeApi>().InvokeAsync(context, purgeLease.Runtime, device, accessToken, purge.Kind, purge.Id);
            }
            if (context.Request.Method == "POST" && relative is "/api/sync/v2/publishers" or "/api/sync/v2/rebaseline" or "/api/sync/v2/batches" or "/api/sync/v2/source-bindings" or "/api/sync/v1/metadata-edits" or "/api/sync/v1/face-review-decisions")
            {
                if (!device.Allows("index.write")) return Results.StatusCode(403);
                await using var writeLease = await pool.AcquireAsync(spaceId, context.RequestAborted);
                return await app.Services.GetRequiredService<SpaceSyncApi>().InvokeAsync(context, relative, writeLease.Runtime, device, accessToken);
            }
            var scope = SpaceRoutePermissions.RequiredScope(context.Request.Method, relative);
            if (scope is null || !device.Allows(scope)) return Results.StatusCode(403);
            await using var lease = await pool.AcquireAsync(spaceId, context.RequestAborted);
            if (lease.Runtime.Purging) { context.Response.Headers.RetryAfter = "2"; return Results.StatusCode(503); }
            if (relative == "/api/gateway/v1/changes" || context.Request.Headers.ContainsKey("X-Picer-Read-Snapshot"))
            {
                using var database = lease.Runtime.OpenDatabase();
                var snapshots = app.Services.GetRequiredService<SpaceReadSnapshots>();
                if (relative == "/api/gateway/v1/changes")
                {
                    var change = await context.Request.ReadFromJsonAsync<Picer.Core.Gateway.GatewayChangeRequest>(context.RequestAborted) ?? throw new ArgumentException();
                    return Results.Json(snapshots.ReadChanges(database, device, change, () => lease.Runtime.Purging));
                }
                return Results.Json(snapshots.ReadCatalog(database, device, context.Request.Headers["X-Picer-Read-Snapshot"].ToString(), relative,
                    context.Request.Query["includeEmbeddings"] == "true", context.Request.Query["includeFaceModels"] == "true"));
            }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted,
                scope == "source.serve" ? CancellationToken.None : lease.Runtime.DataLifetime);
            if (scope != "source.serve") cancellation.CancelAfter(scope == "media.read" ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(5));
            var observer = ObserveRevocationAsync(accounts, device, cancellation);
            try
            {
                if (scope == "source.serve")
                {
                    if (device.Kind != "agent") return Results.StatusCode(403);
                    if (!context.WebSockets.IsWebSocketRequest) return Results.BadRequest();
                    await app.Services.GetRequiredService<SpaceWebSocketRelay>().RelayAsync(context, lease.Runtime, device, relative, cancellation.Token);
                    return Results.Empty;
                }
                var query = context.Request.QueryString;
                if (scope is "thumb.read" or "media.read")
                {
                    using var database = lease.Runtime.OpenDatabase();
                    var agent = SpaceSourceRouting.Resolve(database, relative);
                    query = QueryString.Create(context.Request.Query.Where(item => !item.Key.Equals("agentId", StringComparison.OrdinalIgnoreCase)))
                        .Add("agentId", agent ?? "unbound-source");
                }
                using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), relative + query);
                if (context.Request.ContentLength > 0 || context.Request.Headers.TransferEncoding.Count > 0)
                {
                    request.Content = new StreamContent(context.Request.Body);
                    if (context.Request.ContentType is { } contentType) request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                }
                foreach (var name in new[] { "Range", "If-Range", "If-Match", "If-None-Match", "If-Modified-Since", "Accept" })
                    if (context.Request.Headers.TryGetValue(name, out var value)) request.Headers.TryAddWithoutValidation(name, value.ToArray());
                using var response = await lease.Runtime.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
                context.Response.StatusCode = (int)response.StatusCode;
                foreach (var name in new[] { "ETag", "Accept-Ranges", "Content-Range", "Content-Type", "Content-Length", "Last-Modified", "Retry-After", "X-Picer-Media-Streaming" })
                {
                    if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
                        context.Response.Headers[name] = values.ToArray();
                }
                if (!HttpMethods.IsHead(context.Request.Method)) await response.Content.CopyToAsync(context.Response.Body, cancellation.Token);
                return Results.Empty;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { context.Abort(); return Results.Empty; }
            finally { await cancellation.CancelAsync(); await observer; }
        });
        app.Lifetime.ApplicationStopped.Register(signingKey.Dispose);
        return new(app, accounts, certificate);
    }

    private static async Task ObserveRevocationAsync(AccountStore accounts, AuthorizedDevice device, CancellationTokenSource cancellation)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(cancellation.Token))
                if (!accounts.IsAuthorizationCurrent(device)) { await cancellation.CancelAsync(); return; }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch { await cancellation.CancelAsync(); }
    }
}

public static class SpaceRoutePermissions
{
    public static string? RequiredScope(string method, string path)
    {
        if (method == "GET" && (path == "/api/agent/v1/connect" ||
            path.StartsWith("/api/agent/v1/media/", StringComparison.Ordinal) && path[20..] is { Length: 64 } ticket && ticket.All(Uri.IsHexDigit)))
            return "source.serve";
        if (method == "GET" && path == "/health") return "index.read";
        if (method == "GET" && path == "/api/sync/v1/metadata-edits") return "index.read";
        if (method == "POST" && path == "/api/gateway/v1/media-lease") return "media.read";
        var mediaParts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (method is "GET" or "HEAD" && mediaParts.Length == 6 && mediaParts[0] == "api" && mediaParts[1] == "gateway" &&
            mediaParts[2] == "v1" && mediaParts[3] == "assets" && mediaParts[4] is "images" or "videos" &&
            long.TryParse(mediaParts[5], out var mediaId) && mediaId > 0) return "media.read";
        if (method == "POST" && path is "/api/gateway/v1/search" or "/api/gateway/v1/changes") return "index.read";
        if (method != "GET") return null;
        var components = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length is < 4 or > 5 || components[0] != "api" || components[1] != "gateway" || components[2] != "v1") return null;
        if (components.Length == 5 && (!long.TryParse(components[4], out var id) || id <= 0)) return null;
        return components[3] switch
        {
            "images" or "videos" => "index.read",
            "thumbs" or "face-thumbs" or "video-thumbs" when components.Length == 5 => "thumb.read",
            "previews" or "video-keyframes" when components.Length == 5 => "media.read",
            "sources" or "metadata" or "tags" or "albums" or "people" or "directory-classification" when components.Length == 4 => "index.read",
            _ => null
        };
    }
}
