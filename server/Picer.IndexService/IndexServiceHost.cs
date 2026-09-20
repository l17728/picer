using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Indexing;
using Picer.Core.Logging;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Storage;
using Picer.Core.Thumbnails;
using Picer.Core.Video;

namespace Picer.IndexService;

public sealed record GatewaySearchRequest(SearchFilters? Filters, int PageSize, string? Cursor);
public sealed record SyncImagesRequest(string SourcePath, IReadOnlyList<ImageRecord> Images);

public sealed class IndexApiHandlers(PicerDatabase db, ThumbnailService thumbnails, VideoRepository videos)
{
    public GatewayHealth Health() => new("ok", PicerDatabase.CurrentSchemaVersion, "1.0",
        typeof(IndexApiHandlers).Assembly.GetName().Version?.ToString() ?? "unknown");

    public Task<GatewayPage> ListAsync(long? sourceId, bool includeMissing, int pageSize, string? cursor, CancellationToken cancellationToken) =>
        SearchCoreAsync(new SearchFilters { SourceId = sourceId, IncludeMissing = includeMissing }, pageSize, cursor, cancellationToken);

    public Task<ImageRecord?> GetAsync(long id, CancellationToken cancellationToken) => Task.FromResult(db.GetImage(id));

    public Task<GatewayVideoPage> ListVideosAsync(long? sourceId, int pageSize, string? cursor, CancellationToken cancellationToken)
    {
        var all = videos.GetAll().Where(video => sourceId is null || video.SourceId == sourceId).ToArray();
        var offset = int.TryParse(cursor, out var parsed) ? Math.Max(0, parsed) : 0;
        var size = Math.Clamp(pageSize, 1, 500);
        var items = all.Skip(offset).Take(size).ToArray();
        var next = offset + items.Length < all.Length ? (offset + items.Length).ToString() : null;
        return Task.FromResult(new GatewayVideoPage(items, next, all.Length));
    }

    public Task<VideoRecord?> GetVideoAsync(long id, CancellationToken cancellationToken) =>
        Task.FromResult(videos.GetAll().FirstOrDefault(video => video.Id == id));

    public Task<Stream?> OpenThumbnailAsync(long id, CancellationToken cancellationToken)
    {
        var record = db.GetImage(id);
        if (record is null) return Task.FromResult<Stream?>(null);
        try
        {
            var cached = thumbnails.GetOrCreate(record.Path, id);
            Stream? stream = cached is not null && File.Exists(cached) ? File.OpenRead(cached) : null;
            return Task.FromResult(stream);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or NotSupportedException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    public Task<GatewayPage> SearchAsync(GatewaySearchRequest request, CancellationToken cancellationToken) =>
        SearchCoreAsync(request.Filters ?? new SearchFilters(), request.PageSize, request.Cursor, cancellationToken);

    private async Task<GatewayPage> SearchCoreAsync(SearchFilters filters, int pageSize, string? cursor, CancellationToken cancellationToken)
    {
        var page = await IndexSearchEngine.SearchPagedAsync(db, filters, Math.Clamp(pageSize, 1, 500), GatewayCursors.Decode(cursor), cancellationToken);
        return new GatewayPage(page.Items, page.NextCursor is null ? null : GatewayCursors.Encode(page.NextCursor), page.TotalCount);
    }

    public IReadOnlyList<SourceFolder> ListSources() => db.GetSources();

    public SourceFolder? GetSource(long id) => db.GetSource(id);

    public SourceFolder RegisterSource(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException($"来源目录不存在或不可读：{fullPath}");
        return db.AddSource(fullPath);
    }

    public bool RemoveSource(long id) => db.RemoveSource(id);

    public int RemoveRecycledRecords(IReadOnlyList<long> imageIds)
    {
        var removed = 0;
        foreach (var id in imageIds)
        {
            if (db.GetImage(id) is null) continue;
            db.DeleteImage(id);
            thumbnails.Delete(id);
            removed++;
        }
        return removed;
    }

    public string Backup(string destinationPath)
    {
        var full = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        db.BackupTo(full);
        return full;
    }
}

public sealed record IndexServiceInstance(WebApplication App, X509Certificate2 Certificate, IndexApiHandlers Handlers, IndexEngine Engine, ScanJobManager Jobs);

public static class IndexServiceHost
{
    public static IndexServiceInstance Build(string dataRoot, int port = 8445, string? bearerToken = null)
    {
        Directory.CreateDirectory(dataRoot);
        AppLogger.Initialize(Path.Combine(dataRoot, "logs"));
        AppLogger.Info("Picer.IndexService 启动开始");
        var db = new PicerDatabase(Path.Combine(dataRoot, "picer.db"));
        var thumbnails = new ThumbnailService(Path.Combine(dataRoot, "thumbs"));
        var certificate = ServiceSecurity.EnsureHttpsCertificate(dataRoot);
        var token = bearerToken ?? ServiceSecurity.ResolveToken(dataRoot);

        var builder = WebApplication.CreateBuilder();
        // Keep the secure loopback default for local deployments.  Cross-
        // machine LAN deployments explicitly opt in with
        // PICER_INDEX_SERVICE_BIND=0.0.0.0 (or a specific interface).
        var bindAddress = Environment.GetEnvironmentVariable("PICER_INDEX_SERVICE_BIND");
        if (string.IsNullOrWhiteSpace(bindAddress)) bindAddress = IPAddress.Loopback.ToString();
        if (!IPAddress.TryParse(bindAddress, out var parsedBindAddress))
            throw new InvalidOperationException($"PICER_INDEX_SERVICE_BIND 不是有效的 IP 地址：{bindAddress}");
        builder.WebHost.UseUrls();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(parsedBindAddress, port, listenOptions => listenOptions.UseHttps(certificate)));

        var videoRepository = new VideoRepository(db);
        var handlers = new IndexApiHandlers(db, thumbnails, videoRepository);
        var engine = new IndexEngine(db, new LocalStorageProvider(), thumbnails);
        var jobs = new ScanJobManager();
        var agents = new AgentConnectionRegistry();
        var app = builder.Build();
        app.UseWebSockets();
        app.MapGet("/health", () => Results.Json(handlers.Health()));
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api")) { await next(); return; }
            try { await next(); }
            catch (Exception exception)
            {
                AppLogger.Error("IndexService request failed: " + context.Request.Path, exception);
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsJsonAsync(new { error = exception.GetType().Name, detail = exception.Message });
            }
        });
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                var header = context.Request.Headers.Authorization.ToString();
                var provided = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : "";
                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(token)))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
                    return;
                }
            }
            await next();
        });

        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api/agent/v1/connect"))
            {
                await next();
                return;
            }
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            await agents.HandleAsync(context);
        });

        app.MapGet("/api/gateway/v1/images", async (long? sourceId = null, bool includeMissing = false, int pageSize = 200, string? cursor = null, CancellationToken cancellationToken = default) =>
            Results.Json(await handlers.ListAsync(sourceId, includeMissing, pageSize, cursor, cancellationToken)));
        app.MapGet("/api/gateway/v1/images/{id:long}", async (long id, CancellationToken cancellationToken) =>
        {
            var record = await handlers.GetAsync(id, cancellationToken);
            return record is null ? Results.NotFound() : Results.Json(record);
        });
        app.MapGet("/api/gateway/v1/videos", async (long? sourceId = null, int pageSize = 200, string? cursor = null, CancellationToken cancellationToken = default) =>
            Results.Json(await handlers.ListVideosAsync(sourceId, pageSize, cursor, cancellationToken)));
        app.MapGet("/api/gateway/v1/videos/{id:long}", async (long id, CancellationToken cancellationToken) =>
        {
            var record = await handlers.GetVideoAsync(id, cancellationToken);
            return record is null ? Results.NotFound() : Results.Json(record);
        });
        app.MapGet("/api/gateway/v1/thumbs/{id:long}", async (long id, CancellationToken cancellationToken) =>
        {
            var stream = await handlers.OpenThumbnailAsync(id, cancellationToken);
            return stream is null ? Results.NotFound() : Results.Stream(stream, "image/jpeg");
        });
        app.MapPost("/api/sync/v1/images", (SyncImagesRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.SourcePath)) return Results.BadRequest(new { error = "sourcePath_required" });
            var source = db.AddSource(request.SourcePath);
            var accepted = 0;
            foreach (var image in request.Images)
            {
                image.SourceId = source.Id;
                db.UpsertImage(image);
                accepted++;
            }
            return Results.Json(new { accepted, sourceId = source.Id });
        });
        app.MapGet("/api/gateway/v1/assets/images/{id:long}", async (long id, string? agentId, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(agentId)) return Results.BadRequest(new { error = "agentId_required" });
            var record = await handlers.GetAsync(id, cancellationToken);
            if (record is null) return Results.NotFound();
            var asset = await agents.RequestAsync(agentId, record.Path, cancellationToken);
            return asset is null ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable) : Results.File(asset.Value.Content, asset.Value.ContentType, enableRangeProcessing: true);
        });
        app.MapGet("/api/gateway/v1/assets/videos/{id:long}", async (long id, string? agentId, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(agentId)) return Results.BadRequest(new { error = "agentId_required" });
            var record = await handlers.GetVideoAsync(id, cancellationToken);
            if (record is null) return Results.NotFound();
            var asset = await agents.RequestAsync(agentId, record.Path, cancellationToken);
            return asset is null ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable) : Results.File(asset.Value.Content, "video/mp4", enableRangeProcessing: true);
        });
        app.MapPost("/api/gateway/v1/search", async (GatewaySearchRequest request, CancellationToken cancellationToken) =>
            Results.Json(await handlers.SearchAsync(request, cancellationToken)));
        app.MapGet("/api/gateway/v1/sources", () => Results.Json(handlers.ListSources()));
        app.MapPost("/api/gateway/v1/sources", (GatewaySourceRegistration request) =>
        {
            try { return Results.Json(handlers.RegisterSource(request.Path)); }
            catch (DirectoryNotFoundException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });
        app.MapDelete("/api/gateway/v1/sources/{id:long}", (long id) =>
            handlers.RemoveSource(id) ? Results.NoContent() : Results.NotFound());
        app.MapPost("/api/gateway/v1/scan", (GatewayScanStart request) =>
        {
            var source = handlers.GetSource(request.SourceId);
            return source is null ? Results.NotFound() : Results.Json(new GatewayScanStarted(jobs.Start(engine, source.Id, source.Path)));
        });
        app.MapGet("/api/gateway/v1/jobs/{jobId}", (string jobId) =>
            jobs.Get(jobId) is { } job ? Results.Json(job) : Results.NotFound());
        app.MapPost("/api/gateway/v1/jobs/{jobId}/cancel", (string jobId) =>
            jobs.Cancel(jobId) ? Results.NoContent() : Results.NotFound());
        app.MapPost("/api/gateway/v1/verified-changes", (GatewayVerifiedChangesRequest request) =>
            Results.Json(new GatewayVerifiedChangesResult(handlers.RemoveRecycledRecords(request.ImageIds))));
        app.MapPost("/api/gateway/v1/backup", (GatewayBackupRequest request) =>
            Results.Json(new GatewayBackupResult(handlers.Backup(request.DestinationPath))));

        app.Lifetime.ApplicationStopped.Register(() =>
        {
            AppLogger.Info("Picer.IndexService 已停止");
            db.Dispose();
        });
        return new IndexServiceInstance(app, certificate, handlers, engine, jobs);
    }

    public static async Task<string> StartAsync(WebApplication app)
    {
        await app.StartAsync();
        var baseUrl = GetBaseUrl(app);
        AppLogger.Info($"Picer.IndexService 已监听 {baseUrl}");
        return baseUrl;
    }

    public static string GetBaseUrl(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        return addresses.First(address => address.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }
}
