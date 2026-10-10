using System.Net;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Indexing;
using Picer.Core.Logging;
using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Search;
using Picer.Core.Storage;
using Picer.Core.Thumbnails;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.IndexService;

public sealed record GatewaySearchRequest(SearchFilters? Filters, int PageSize, string? Cursor);
public sealed record ServiceCapability(string Id, string State, string ExecutionNode, string? Reason = null);
public sealed record SyncImagesRequest(string SourcePath, IReadOnlyList<ImageRecord> Images);
public sealed record SyncVideosRequest(string SourcePath, IReadOnlyList<VideoRecord> Videos);
public sealed record SyncMetadataRequest(
    IReadOnlyList<Tag> Tags,
    IReadOnlyList<Album> Albums,
    IReadOnlyList<AlbumImageLink> AlbumImages,
    IReadOnlyList<ImageTagLink> ImageTags,
    IReadOnlyList<ImageLocationHierarchyRow> Locations,
    IReadOnlyList<PersonSummary>? People = null,
    IReadOnlyList<GatewayDetectedFace>? Faces = null,
    IReadOnlyList<GatewayFaceAssignment>? FaceAssignments = null,
    IReadOnlyList<GatewayVideoKeyframe>? VideoKeyframes = null,
    IReadOnlyList<GatewayVideoTranscript>? VideoTranscripts = null,
    IReadOnlyList<GatewayVideoSummary>? VideoSummaries = null,
    IReadOnlyList<GatewayVideoFace>? VideoFaces = null,
    IReadOnlyList<GatewayCloudVisionAnalysis>? CloudVisionAnalyses = null,
    IReadOnlyList<GatewaySceneGroupDescription>? SceneGroupDescriptions = null,
    IReadOnlyList<GatewayFaceReviewState>? FaceReviewStates = null,
    IReadOnlyList<GatewayFaceCandidateCluster>? FaceCandidateClusters = null,
    IReadOnlyList<GatewayFaceCandidateMember>? FaceCandidateMembers = null,
    IReadOnlyList<GatewayFaceTrainingArtifact>? FaceTrainingArtifacts = null,
    IReadOnlyList<GatewayFacePersonPrototype>? FacePersonPrototypes = null,
    IReadOnlyList<GatewayFacePersonPolicy>? FacePersonPolicies = null,
    IReadOnlyList<GatewayFaceModelSetting>? FaceModelSettings = null);
public sealed record SyncFacesRequest(
    IReadOnlyList<GatewayDetectedFace> Faces,
    IReadOnlyList<GatewayFaceAssignment> Assignments);
public sealed record SyncDirectoryClassificationRequest(IReadOnlyDictionary<string, bool> Overrides);
public sealed record AlbumImageLink(long AlbumId, long ImageId);
public sealed record ImageTagLink(long ImageId, long TagId);

public sealed class IndexApiHandlers(PicerDatabase db, ThumbnailService thumbnails, VideoRepository videos)
{
    public Func<long, CancellationToken, Task<bool>>? VideoPurgeHandler { get; set; }
    public Task<bool> PurgeVideoAsync(long id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (VideoPurgeHandler is not null) return VideoPurgeHandler(id, token);
        using var independent = db.OpenIndependentConnection();
        return Task.FromResult(new VideoRecordPurger(independent).Purge(id));
    }
    public bool PurgeMissingImage(long id)
    { using var independent = db.OpenIndependentConnection(); return new ImageRecordPurger(independent).Purge(id); }
    internal static string NormalizeAgentPath(string path)
    {
        // Mirrored source paths are stored under the service's Linux data root
        // (for example /opt/picer-index/G:\\Apple照片).  The MediaAgent runs
        // on the Windows source host and must receive the original Windows
        // path, not the server storage prefix.
        return Picer.Core.Indexing.MediaSourcePath.ForStorage(path);
    }
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

    public Task<VideoRecord?> GetVideoAsync(long id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = db.OpenIndependentConnection();
        return Task.FromResult(new VideoRepository(connection).Get(id));
    }

    public bool IsCurrentVideo(VideoRecord expected)
    {
        using var connection = db.OpenIndependentConnection();
        return new VideoRepository(connection).IsCurrentContent(expected.Id, expected);
    }

    public IReadOnlyList<VideoKeyframe> GetVideoKeyframes(long videoId)
    {
        using var command = db.Command("SELECT id,timestamp_seconds,path,dhash FROM video_keyframes WHERE video_id=$video ORDER BY timestamp_seconds,id");
        command.Parameters.AddWithValue("$video", new VideoContentRepository(db).GetContentId(videoId));
        using var reader = command.ExecuteReader();
        var frames = new List<VideoKeyframe>();
        while (reader.Read())
        {
            frames.Add(new VideoKeyframe(
                reader.GetDouble(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(0)));
        }
        return frames;
    }

    public IReadOnlyList<VideoKeyframe> SaveGeneratedKeyframes(long videoId, IReadOnlyList<VideoKeyframe> frames, VideoRecord? expected = null)
    {
        using var connection = db.OpenIndependentConnection();
        using var transaction = connection.BeginTransaction();
        if (expected is not null && !new VideoRepository(connection).IsCurrentContent(videoId, expected)) throw new InvalidOperationException("video_content_changed");
        using (var clear = connection.Command("DELETE FROM video_keyframes WHERE video_id=$video"))
        {
            clear.Transaction = transaction;
            clear.Parameters.AddWithValue("$video", new VideoContentRepository(connection).GetContentId(videoId));
            clear.ExecuteNonQuery();
        }
        var saved = new List<VideoKeyframe>(frames.Count);
        foreach (var frame in frames)
        {
            using var insert = connection.Command("INSERT INTO video_keyframes(video_id,timestamp_seconds,path,dhash,created_utc) VALUES($video,$time,$path,$hash,$now)");
            insert.Transaction = transaction;
            insert.Parameters.AddWithValue("$video", new VideoContentRepository(connection).GetContentId(videoId));
            insert.Parameters.AddWithValue("$time", frame.TimestampSeconds);
            insert.Parameters.AddWithValue("$path", frame.Path);
            insert.Parameters.AddWithValue("$hash", frame.DifferenceHash);
            insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
            using var idCommand = connection.Command("SELECT last_insert_rowid()");
            idCommand.Transaction = transaction;
            var generatedId = Convert.ToInt64(idCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            saved.Add(frame with { Id = generatedId });
        }
        transaction.Commit();
        return saved;
    }

    public string? GetFirstVideoKeyframePath(long id)
    {
        using var command = db.Command("SELECT path FROM video_keyframes WHERE video_id=$video ORDER BY timestamp_seconds,id LIMIT 1");
        command.Parameters.AddWithValue("$video", new VideoContentRepository(db).GetContentId(id));
        return command.ExecuteScalar() as string;
    }

    public string? GetVideoKeyframePath(long id)
    {
        using var command = db.Command("SELECT path FROM video_keyframes WHERE id=$id");
        command.Parameters.AddWithValue("$id", id);
        var path = command.ExecuteScalar() as string;
        // Some SQLite builds return an empty scalar while the same row is
        // visible through a reader (notably while the mirrored WAL is being
        // checkpointed). Use the metadata reader as a safe fallback.
        return path ?? db.GetAllMirroredVideoKeyframes().FirstOrDefault(frame => frame.Id == id)?.Path;
    }

    public string? GetVideoKeyframePathByOrdinal(long videoId, int ordinal)
    {
        using var command = db.Command("SELECT path FROM video_keyframes WHERE video_id=$video ORDER BY timestamp_seconds,id LIMIT 1 OFFSET $offset");
        command.Parameters.AddWithValue("$video", new VideoContentRepository(db).GetContentId(videoId));
        command.Parameters.AddWithValue("$offset", Math.Max(0, ordinal));
        var path = command.ExecuteScalar() as string;
        return path ?? db.GetAllMirroredVideoKeyframes()
            .Where(frame => frame.VideoId == videoId)
            .OrderBy(frame => frame.TimestampSeconds).ThenBy(frame => frame.Id)
            .Skip(Math.Max(0, ordinal)).Select(frame => frame.Path).FirstOrDefault();
    }

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
    public GatewayChangePage GetChanges(GatewayChangeRequest request)
    {
        using var independent = db.OpenIndependentConnection();
        return independent.ReadMirrorChanges(request);
    }
    public IReadOnlyList<Tag> ListTags() => SemanticEngine.GetTags(db);
    public IReadOnlyList<Album> ListAlbums() => SemanticEngine.GetAlbums(db);
    public GatewayMetadata GetMetadata(bool includeEmbeddings = true, bool includeFaceModels = true)
    {
        var albumImages = new List<GatewayAlbumImageLink>();
        using (var command = db.Command("SELECT ai.album_id,ai.image_id FROM album_images ai JOIN images i ON i.id=ai.image_id WHERE i.status='active'"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) albumImages.Add(new(reader.GetInt64(0), reader.GetInt64(1)));
        var imageTags = new List<GatewayImageTagLink>();
        using (var command = db.Command("SELECT image_id,tag_id FROM image_tags"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) imageTags.Add(new(reader.GetInt64(0), reader.GetInt64(1)));
        var faces = new List<GatewayDetectedFace>();
        using (var command = db.Command("SELECT id,image_id,model_version,ordinal,box_x,box_y,box_width,box_height,confidence,landmarks_json,embedding,created_utc,is_archived FROM detected_faces"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                faces.Add(new GatewayDetectedFace(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt32(3),
                    reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6), reader.GetDouble(7), reader.GetDouble(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9), includeEmbeddings && !reader.IsDBNull(10) ? (byte[])reader[10] : null, reader.GetString(11), reader.GetBoolean(12)));
        }
        var assignments = new List<GatewayFaceAssignment>();
        using (var command = db.Command("SELECT face_id,person_id,state,confidence,assigned_by,updated_utc FROM face_person_assignments"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                assignments.Add(new GatewayFaceAssignment(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetDouble(3), reader.GetString(4), reader.GetString(5)));
        }
        var keyframes = new List<GatewayVideoKeyframe>();
        using (var command = db.Command("SELECT id,(SELECT MIN(id) FROM videos WHERE content_id=video_keyframes.video_id),timestamp_seconds,path,dhash FROM video_keyframes"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) keyframes.Add(new GatewayVideoKeyframe(reader.GetInt64(0), reader.GetInt64(1), reader.GetDouble(2), reader.GetString(3), reader.GetInt64(4)));
        var transcripts = new List<GatewayVideoTranscript>();
        using (var command = db.Command("SELECT (SELECT MIN(id) FROM videos WHERE content_id=video_transcripts.video_id),model_version,transcript_json FROM video_transcripts"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) transcripts.Add(new GatewayVideoTranscript(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        var summaries = new List<GatewayVideoSummary>();
        using (var command = db.Command("SELECT (SELECT MIN(id) FROM videos WHERE content_id=video_summaries.video_id),provider,model_version,prompt_version,summary,result_json,source_file_size,source_modified_utc FROM video_summaries"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) summaries.Add(new GatewayVideoSummary(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetString(7)));
        var videoFaces = db.GetAllMirroredVideoFaces()
            .Select(face => includeEmbeddings ? face : face with { Embedding = null }).ToArray();
        var analyses = new List<GatewayCloudVisionAnalysis>();
        using (var command = db.Command("SELECT image_id,provider,model_version,prompt_version,status,summary,result_json,updated_utc,source_file_size,source_modified_utc FROM cloud_vision_analyses WHERE status='completed'"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) analyses.Add(new GatewayCloudVisionAnalysis(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7), reader.GetInt64(8), reader.GetString(9)));
        var scenes = new List<GatewaySceneGroupDescription>();
        using (var command = db.Command("SELECT fingerprint,directory_path,start_utc,end_utc,image_ids_json,representative_image_ids_json,provider,model_version,prompt_version,scene_name,summary,search_terms_json,state,created_utc,reviewed_utc FROM scene_group_descriptions"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) scenes.Add(new GatewaySceneGroupDescription(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11), reader.GetString(12), reader.GetString(13), reader.GetString(14)));
        var reviews = db.GetAllMirroredFaceReviewStates();
        var clusters = db.GetAllMirroredFaceCandidateClusters();
        var members = db.GetAllMirroredFaceCandidateMembers();
        var trainingArtifacts = new List<GatewayFaceTrainingArtifact>();
        if (includeFaceModels)
        {
            using var command = db.Command("SELECT artifact_id,dataset_id,endpoint,model_version,artifact_json,false_accept_rate,false_reject_rate,state,created_utc,applied_utc FROM face_training_artifacts");
            using var reader = command.ExecuteReader();
            while (reader.Read()) trainingArtifacts.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetDouble(5), reader.GetDouble(6), reader.GetString(7), reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        var prototypes = new List<GatewayFacePersonPrototype>();
        if (includeFaceModels)
        {
            using var command = db.Command("SELECT person_id,centroid,threshold,sample_count,model_version,updated_utc FROM face_person_prototypes");
            using var reader = command.ExecuteReader();
            while (reader.Read()) prototypes.Add(new(reader.GetInt64(0), (byte[])reader[1], reader.GetDouble(2), reader.GetInt32(3), reader.GetString(4), reader.GetString(5)));
        }
        var policies = new List<GatewayFacePersonPolicy>();
        if (includeFaceModels)
        {
            using var command = db.Command("SELECT person_id,artifact_id,model_version,prototype,match_threshold,updated_utc FROM face_person_policies");
            using var reader = command.ExecuteReader();
            while (reader.Read()) policies.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), (byte[])reader[3], reader.GetDouble(4), reader.GetString(5)));
        }
        var modelSettings = new List<GatewayFaceModelSetting>();
        if (includeFaceModels)
        {
            using var command = db.Command("SELECT model_version,match_threshold,calibrated_same_pairs,calibrated_different_pairs,updated_utc FROM face_model_settings");
            using var reader = command.ExecuteReader();
            while (reader.Read()) modelSettings.Add(new(reader.GetString(0), reader.GetDouble(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4)));
        }
        return new GatewayMetadata(albumImages, imageTags, db.GetImageLocationHierarchyRows(), faces, assignments, keyframes, transcripts,
            summaries, videoFaces, analyses, scenes, reviews, clusters, members,
            includeFaceModels ? trainingArtifacts : null, includeFaceModels ? prototypes : null,
            includeFaceModels ? policies : null, includeFaceModels ? modelSettings : null, db.GetFaceReviewDecisions());
    }
    public IReadOnlyList<PersonSummary> ListPeople() => new PeopleService(db).GetAll();

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
        builder.Services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.MimeTypes = ["application/json"];
        });
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
        var journal = new TaskJournal(dataRoot);
        journal.MarkInterrupted();
        var configPath = Path.Combine(dataRoot, "service-config.json");
        var configHistory = Path.Combine(dataRoot, "service-config-history");
        Directory.CreateDirectory(configHistory);
        JsonElement? savedServiceConfig = null;
        if (File.Exists(configPath))
        {
            try
            {
                using var saved = JsonDocument.Parse(File.ReadAllBytes(configPath));
                if (saved.RootElement.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object)
                    savedServiceConfig = config.Clone();
                AppLogger.Info($"Service configuration loaded; hasPersistedConfig={savedServiceConfig.HasValue}");
            }
            catch (JsonException exception)
            {
                AppLogger.Warn($"Persisted service configuration is invalid; agents will register without replay: {exception.GetType().Name}");
            }
        }
        var agents = new AgentConnectionRegistry(journal, savedServiceConfig);
        var mediaPreviews = new MediaPreviewCache();
        var app = builder.Build();
        app.UseWebSockets();
        app.MapPost("/api/gateway/v1/media-lease", async (MediaLeaseRequest request, CancellationToken token) =>
        {
            await agents.ManageMediaLeaseAsync(request.ETag, request.Release, token);
            return Results.NoContent();
        });
        app.UseResponseCompression();
        app.MapGet("/service", () => Results.Content("""
<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><title>Picer Service</title><style>body{font:14px sans-serif;max-width:900px;margin:30px auto;color:#222}table{border-collapse:collapse;width:100%}td,th{border:1px solid #ddd;padding:8px;text-align:left}.ok{color:#087f23}.bad{color:#a11}</style></head>
<body><h1>Picer 统一 Service</h1><p id="summary">正在读取状态…</p><table><thead><tr><th>能力</th><th>状态</th><th>执行节点</th><th>原因</th></tr></thead><tbody id="capabilities"></tbody></table><h2>配置状态</h2><pre id="config"></pre><script>
async function load(){const [c,g]=await Promise.all([fetch('/api/service/v1/capabilities'),fetch('/api/service/v1/config')]);const cap=await c.json(),cfg=await g.json();document.getElementById('summary').textContent=`在线代理：${cap.agentOnline?'是':'否'}；模式：${cap.mode}`;document.getElementById('capabilities').innerHTML=cap.capabilities.map(x=>`<tr><td>${x.id}</td><td class="${x.state==='ready'?'ok':'bad'}">${x.state}</td><td>${x.executionNode}</td><td>${x.reason||''}</td></tr>`).join('');document.getElementById('config').textContent=JSON.stringify(cfg,null,2)}load();setInterval(load,5000);
</script></body></html>
""", "text/html; charset=utf-8"));
        app.MapGet("/health", () => Results.Json(handlers.Health()));
        app.MapGet("/version", () =>
        {
            var assembly = typeof(IndexApiHandlers).Assembly;
            var deploymentPath = Path.Combine(AppContext.BaseDirectory, "deployment.json");
            string? deploymentId = null;
            string? binarySha256 = null;
            string? sourceCommit = null;
            string? builtUtc = null;
            if (File.Exists(deploymentPath))
            {
                try
                {
                    using var deployment = JsonDocument.Parse(File.ReadAllBytes(deploymentPath));
                    var root = deployment.RootElement;
                    deploymentId = root.TryGetProperty("deploymentId", out var id) ? id.GetString() : null;
                    binarySha256 = root.TryGetProperty("binarySha256", out var hash) ? hash.GetString() : null;
                    sourceCommit = root.TryGetProperty("sourceCommit", out var commit) ? commit.GetString() : null;
                    builtUtc = root.TryGetProperty("builtUtc", out var built) ? built.GetString() : null;
                }
                catch (JsonException exception)
                {
                    AppLogger.Warn($"Deployment manifest is invalid; type={exception.GetType().Name}");
                }
            }

            return Results.Json(new
            {
                service = "Picer.IndexService",
                assemblyVersion = assembly.GetName().Version?.ToString() ?? "unknown",
                informationalVersion = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion,
                deploymentId,
                binarySha256,
                sourceCommit,
                builtUtc,
                startedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime()
            });
        });
        app.MapGet("/api/service/v1/capabilities", () =>
        {
            var agentOnline = agents.OnlineCount > 0;
            var agentCapabilities = agents.OnlineCapabilities;
            var capabilities = new[]
            {
                new ServiceCapability("ffmpeg.probe", agentCapabilities.Contains("ffmpeg.probe") ? "ready" : "unavailable", "media-agent", agentCapabilities.Contains("ffmpeg.probe") ? null : "media_agent_capability_missing"),
                new ServiceCapability("ffmpeg.keyframes", agentCapabilities.Contains("ffmpeg.keyframes") ? "ready" : "unavailable", "media-agent", agentCapabilities.Contains("ffmpeg.keyframes") ? null : "media_agent_capability_missing"),
                new ServiceCapability("ffmpeg.audio_extract", agentCapabilities.Contains("ffmpeg.audio_extract") ? "ready" : "unavailable", "media-agent", agentCapabilities.Contains("ffmpeg.audio_extract") ? null : "media_agent_capability_missing"),
                new ServiceCapability("asr.transcribe", agentCapabilities.Contains("asr.transcribe") ? "ready" : "unavailable", "media-agent", agentCapabilities.Contains("asr.transcribe") ? null : "media_agent_capability_missing"),
                new ServiceCapability("vl.analyze", agentCapabilities.Contains("vl.analyze") ? "ready" : "unavailable", "media-agent", agentCapabilities.Contains("vl.analyze") ? null : (agentOnline ? "vl_endpoint_not_configured" : "media_agent_offline")),
                new ServiceCapability("face.detect", agentCapabilities.Contains("face.detect") ? "ready" : "unavailable", "media-agent", agentCapabilities.Contains("face.detect") ? null : "media_agent_capability_missing"),
                new ServiceCapability("training.proxy", agentCapabilities.Contains("training.proxy") ? "configured" : "unavailable", "media-agent", agentCapabilities.Contains("training.proxy") ? "check_algorithms_for_model_readiness" : "training_endpoint_not_configured"),
                new ServiceCapability("face.embed", agentCapabilities.Contains("face.embed") ? "ready" : "unavailable", "media-agent", agentCapabilities.Contains("face.embed") ? null : "media_agent_capability_missing"),
                new ServiceCapability("tts.synthesize", agentCapabilities.Contains("tts.synthesize") ? "ready" : "unavailable", "media-agent", agentCapabilities.Contains("tts.synthesize") ? null : "media_agent_capability_missing")
            };
            return Results.Json(new { mode = "service", agentOnline, capabilities });
        });
        app.MapGet("/api/service/v1/config", () =>
        {
            var online = agents.OnlineCapabilities;
            return Results.Json(new
            {
                mode = "service",
                executionNode = agents.OnlineCount > 0 ? "media-agent" : "none",
                ffmpeg = new { configured = online.Contains("ffmpeg.probe"), source = "media-agent" },
                asr = new { configured = online.Contains("asr.transcribe"), source = "media-agent" },
                vl = new { configured = online.Contains("vl.analyze"), source = "media-agent" },
                tts = new { configured = online.Contains("tts.synthesize"), source = "media-agent" },
                face = new { configured = online.Contains("face.embed"), source = "media-agent" },
                credentials = new { storedOnService = true, exposedToClient = false }
            });
        });
        app.MapPut("/api/service/v1/config", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Results.BadRequest(new { error = "config_object_required" });
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name.Contains("key", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("secret", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("token", StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { error = "credentials_must_be_configured_on_service_agent" });
            var mergedConfig = new JsonObject();
            if (File.Exists(configPath))
            {
                try
                {
                    var existing = JsonNode.Parse(await File.ReadAllBytesAsync(configPath, cancellationToken));
                    if (existing?["config"] is JsonObject existingConfig)
                        mergedConfig = (JsonObject)existingConfig.DeepClone();
                }
                catch (JsonException)
                {
                    return Results.Problem("persisted_service_config_invalid", statusCode: StatusCodes.Status500InternalServerError);
                }
            }
            foreach (var property in document.RootElement.EnumerateObject())
                mergedConfig[property.Name] = JsonNode.Parse(property.Value.GetRawText());

            var version = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
            var updatedUtc = DateTime.UtcNow;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version, updatedUtc, config = mergedConfig });
            if (File.Exists(configPath)) File.Copy(configPath, Path.Combine(configHistory, Path.GetFileName(configPath) + "." + version), true);
            await File.WriteAllBytesAsync(configPath, bytes, cancellationToken);
            File.Copy(configPath, Path.Combine(configHistory, Path.GetFileName(configPath) + "." + version), true);
            await agents.BroadcastConfigAsync(document.RootElement, cancellationToken);
            return Results.Json(new { version, persisted = true, applied = agents.OnlineCount > 0, note = "已广播给在线 MediaAgent；新连接将重新读取配置" });
        });
        app.MapGet("/api/service/v1/config/history", () => Results.Json(Directory.EnumerateFiles(configHistory).OrderDescending().Select(Path.GetFileName).ToArray()));
        app.MapGet("/api/service/v1/tasks", (int? limit) => Results.Json(journal.Read(Math.Clamp(limit ?? 200, 1, 2000))));
        var taskRunner = new ServiceTaskRunner(dataRoot, journal, certificate, token, () =>
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            var uri = new UriBuilder(address);
            if (uri.Host is "0.0.0.0" or "[::]") uri.Host = "127.0.0.1";
            return uri.Uri;
        });
        handlers.VideoPurgeHandler = (id, cancellationToken) => taskRunner.PurgeVideoAsync(id, async token =>
        {
            using var independent = db.OpenIndependentConnection();
            var purger = new VideoRecordPurger(independent);
            var snapshot = purger.Inspect(id);
            if (snapshot is null) return true;
            using var removal = await VideoOperationLifetime.BeginPurgeAsync(snapshot.Path, token);
            using var transaction = independent.BeginTransaction();
            snapshot = purger.Inspect(id);
            if (snapshot is null) return true;
            {
                var sourcePath = IndexApiHandlers.NormalizeAgentPath(snapshot.Path);
                if (!File.Exists(sourcePath) || snapshot.Keyframes.Any(frame => frame.Contains(".picer-keyframes", StringComparison.OrdinalIgnoreCase)))
                {
                    var frames = snapshot.Keyframes.Where(frame => frame.Contains(".picer-keyframes", StringComparison.OrdinalIgnoreCase)).ToArray();
                    var result = await agents.RequestTaskAsync(null, "video.purge-cache", JsonSerializer.Serialize(new VideoRecordPurger.SourceCacheRequest(sourcePath, frames, purger.GetProtectedKeyframes(snapshot))), token);
                    if (!string.Equals(result?.Trim(), "true", StringComparison.Ordinal)) throw new IOException("video_cache_purge_not_confirmed");
                }
            }
            var purged = purger.Purge(id, transactionHeld: true);
            transaction.Commit(); return purged;
        }, cancellationToken);
        app.Lifetime.ApplicationStopped.Register(taskRunner.Dispose);
        app.MapGet("/api/service/v1/tasks/{requestId}/result", (string requestId) => taskRunner.GetResult(requestId));
        app.MapPost("/api/service/v1/tasks/{requestId}/retry", (string requestId, CancellationToken cancellationToken) =>
            taskRunner.RetryAsync(requestId, cancellationToken));
        app.MapPost("/api/service/v1/config/rollback/{version}", async (string version, CancellationToken cancellationToken) =>
        {
            var snapshot = Path.Combine(configHistory, "service-config.json." + version);
            if (!File.Exists(snapshot)) return Results.NotFound(new { error = "config_version_not_found" });
            File.Copy(snapshot, configPath, true);
            using var restored = JsonDocument.Parse(await File.ReadAllTextAsync(configPath, cancellationToken));
            if (restored.RootElement.TryGetProperty("config", out var restoredConfig)) await agents.BroadcastConfigAsync(restoredConfig, cancellationToken);
            await Task.CompletedTask;
            return Results.Json(new { version, restored = true, applied = agents.OnlineCount > 0, note = "已广播给在线 MediaAgent" });
        });
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api")) { await next(); return; }
            try { await next(); }
            catch (Exception exception)
            {
                if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested)
                {
                    context.Abort();
                    return;
                }
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

        app.Use((context, next) => taskRunner.InvokeAsync(context, next));

        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/agent/v1/media", out var mediaTicket))
            {
                await agents.HandleMediaAsync(context, mediaTicket.Value?.Trim('/') ?? "");
                return;
            }
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
        // The default remote mirror payload deliberately omits embeddings. Face
        // boxes, albums and video metadata become available quickly; vectors
        // remain available to an explicit trusted caller when needed.
        app.MapGet("/api/gateway/v1/metadata", (bool includeEmbeddings = false, bool includeFaceModels = false) => Results.Json(handlers.GetMetadata(includeEmbeddings, includeFaceModels)));
        app.MapGet("/api/gateway/v1/thumbs/{id:long}", async (long id, string? agentId, CancellationToken cancellationToken) =>
        {
            var stream = await handlers.OpenThumbnailAsync(id, cancellationToken);
            if (stream is null)
            {
                var image = await handlers.GetAsync(id, cancellationToken);
                if (image is not null)
                {
                    var key = $"photo:{agentId}:{image.Sha256 ?? image.Path}:{image.FileSize}:{image.ModifiedUtc.Ticks}:256";
                    var bytes = await mediaPreviews.GetAsync(key, token => MediaAssetResults.PreviewAsync(agents, agentId, IndexApiHandlers.NormalizeAgentPath(image.Path), 256, token), cancellationToken);
                    stream = new MemoryStream(bytes, writable: false);
                }
            }
            return stream is null ? Results.NotFound() : Results.Stream(stream, "image/jpeg");
        });
        app.MapGet("/api/gateway/v1/previews/{id:long}", async (long id, string? agentId, CancellationToken cancellationToken) =>
        {
            var image = await handlers.GetAsync(id, cancellationToken);
            if (image is null) return Results.NotFound();
            var key = $"photo:{agentId}:{image.Sha256 ?? image.Path}:{image.FileSize}:{image.ModifiedUtc.Ticks}:1600";
            var bytes = await mediaPreviews.GetAsync(key, token => MediaAssetResults.PreviewAsync(agents, agentId, IndexApiHandlers.NormalizeAgentPath(image.Path), 1600, token), cancellationToken);
            return Results.File(bytes, "image/jpeg");
        });
        app.MapGet("/api/gateway/v1/face-thumbs/{id:long}", async (long id, string? agentId, CancellationToken cancellationToken) =>
        {
            using var command = db.Command("SELECT image_id,box_x,box_y,box_width,box_height FROM detected_faces WHERE id=$id");
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return Results.NotFound();
            var image = db.GetImage(reader.GetInt64(0));
            if (image is null) return Results.NotFound();
            var box = new NormalizedFaceBox((float)reader.GetDouble(1), (float)reader.GetDouble(2), (float)reader.GetDouble(3), (float)reader.GetDouble(4));
            Stream? original = File.Exists(image.Path) ? File.OpenRead(image.Path) : null;
            if (original is null)
            {
                var key = $"photo:{agentId}:{image.Sha256 ?? image.Path}:{image.FileSize}:{image.ModifiedUtc.Ticks}:1600";
                var bytes = await mediaPreviews.GetAsync(key, token => MediaAssetResults.PreviewAsync(agents, agentId, IndexApiHandlers.NormalizeAgentPath(image.Path), 1600, token), cancellationToken);
                original = new MemoryStream(bytes, writable: false);
            }
            if (original is null) return Results.NotFound();
            await using (original)
            {
                var crop = FaceThumbnailCropper.Crop(original, box, cancellationToken);
                return crop is null ? Results.NotFound() : Results.Stream(crop, "image/jpeg");
            }
        });
        app.MapGet("/api/gateway/v1/video-thumbs/{id:long}", async (long id, string? agentId, CancellationToken cancellationToken) =>
        {
            var path = handlers.GetFirstVideoKeyframePath(id);
            Stream? stream = !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? File.OpenRead(path) : null;
            if (stream is null && !string.IsNullOrWhiteSpace(path))
            {
                var bytes = await mediaPreviews.GetAsync($"video:{agentId}:{path}", token => MediaAssetResults.PreviewAsync(agents, agentId, IndexApiHandlers.NormalizeAgentPath(path), 256, token), cancellationToken);
                stream = new MemoryStream(bytes, writable: false);
            }
            return stream is null ? Results.NotFound() : Results.Stream(stream, "image/jpeg");
        });
        app.MapPost("/api/sync/v1/images", (SyncImagesRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.SourcePath)) return Results.BadRequest(new { error = "sourcePath_required" });
            var source = db.AddSource(request.SourcePath, reconcileAliases: true);
            var accepted = 0;
            foreach (var image in request.Images)
            {
                image.SourceId = source.Id;
                db.UpsertMirroredImage(image);
                accepted++;
            }
            return Results.Json(new { accepted, sourceId = source.Id });
        });
        app.MapPost("/api/sync/v1/videos", (SyncVideosRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.SourcePath)) return Results.BadRequest(new { error = "sourcePath_required" });
            var source = db.AddSource(request.SourcePath, reconcileAliases: true);
            foreach (var video in request.Videos) videoRepository.UpsertRemote(video, source.Id);
            return Results.Json(new { accepted = request.Videos.Count, sourceId = source.Id });
        });
        app.MapPost("/api/sync/v1/metadata", (SyncMetadataRequest request) =>
        {
            foreach (var tag in request.Tags) db.UpsertMirroredTag(tag);
            foreach (var album in request.Albums) db.UpsertMirroredAlbum(album);
            foreach (var person in request.People ?? []) db.UpsertMirroredPerson(person);
            foreach (var face in request.Faces ?? []) db.UpsertMirroredFace(face);
            foreach (var assignment in request.FaceAssignments ?? []) db.UpsertMirroredFaceAssignment(assignment);
            db.UpsertMirroredVideoMetadata(new GatewayMetadata([], [], [], [], [], request.VideoKeyframes ?? [], request.VideoTranscripts ?? [], request.VideoSummaries ?? [], request.VideoFaces ?? [], request.CloudVisionAnalyses ?? [], request.SceneGroupDescriptions ?? []));
            db.ReplaceMirroredFaceWorkflow(new GatewayMetadata([], [], [], [], [], [], [], [], null, null, null,
                request.FaceReviewStates, request.FaceCandidateClusters, request.FaceCandidateMembers));
            if (request.FaceTrainingArtifacts is not null && request.FacePersonPrototypes is not null && request.FacePersonPolicies is not null && request.FaceModelSettings is not null)
                db.UpsertMirroredFaceTrainingMetadata(new GatewayMetadata([], [], [], [], [], [], [], [], null, null, null,
                    null, null, null, request.FaceTrainingArtifacts, request.FacePersonPrototypes, request.FacePersonPolicies, request.FaceModelSettings));
            foreach (var link in request.AlbumImages) SemanticEngine.AddToAlbum(db, link.AlbumId, link.ImageId, recordEdit: false);
            foreach (var link in request.ImageTags) db.UpsertMirroredImageTag(link.ImageId, link.TagId);
            foreach (var location in request.Locations) db.UpsertMirroredLocation(location);
            db.ReplayMetadataEdits();
            return Results.Json(new { tags = request.Tags.Count, albums = request.Albums.Count,
                albumImages = request.AlbumImages.Count, imageTags = request.ImageTags.Count,
                locations = request.Locations.Count, people = request.People?.Count ?? 0 });
        });
        app.MapGet("/api/sync/v1/metadata-edits", () =>
        {
            using var connection = db.OpenIndependentConnection();
            return Results.Json(connection.GetMetadataEdits());
        });
        app.MapPost("/api/sync/v1/metadata-edits", (MetadataEdit[] edits) =>
        {
            if (edits.Length > 500) return Results.BadRequest(new { error = "metadata_batch_too_large" });
            try
            {
                using var connection = db.OpenIndependentConnection();
                return Results.Json(connection.MergeMetadataEdits(edits));
            }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_metadata_edit" }); }
        });
        app.MapPost("/api/sync/v1/face-review-decisions", (GatewayFaceReviewDecision[] decisions) =>
        {
            if (decisions.Length > 500) return Results.BadRequest(new { error = "face_review_batch_too_large" });
            try
            {
                using var reviewDatabase = db.OpenIndependentConnection();
                return Results.Json(reviewDatabase.MergeFaceReviewDecisions(decisions));
            }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_face_review_decision" }); }
        });
        app.MapPost("/api/sync/v1/faces", (SyncFacesRequest request) =>
        {
            db.UpsertMirroredFaces(request.Faces, request.Assignments);
            return Results.Json(new { faces = request.Faces.Count, assignments = request.Assignments.Count });
        });
        app.MapGet("/api/gateway/v1/directory-classification", () =>
        {
            var path = Path.Combine(dataRoot, "directory-classification.json");
            try
            {
                if (!File.Exists(path)) return Results.Json(new Dictionary<string, bool>());
                return Results.Json(JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(path)) ?? new());
            }
            catch (Exception exception)
            {
                AppLogger.Warn("读取目录场景分类偏好失败：" + exception.Message);
                return Results.Json(new Dictionary<string, bool>());
            }
        });
        app.MapPost("/api/sync/v1/directory-classification", (SyncDirectoryClassificationRequest request) =>
        {
            var path = Path.Combine(dataRoot, "directory-classification.json");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(request.Overrides));
            File.Move(temporary, path, true);
            return Results.Json(new { count = request.Overrides.Count });
        });
        app.MapMethods("/api/gateway/v1/assets/images/{id:long}", ["GET", "HEAD"], async (long id, string? agentId, HttpContext context, CancellationToken cancellationToken) =>
        {
            var record = await handlers.GetAsync(id, cancellationToken);
            if (record is null) return Results.NotFound();
            return await MediaAssetResults.OpenAsync(agents, context, agentId, IndexApiHandlers.NormalizeAgentPath(record.Path));
        });
        app.MapMethods("/api/gateway/v1/assets/videos/{id:long}", ["GET", "HEAD"], async (long id, string? agentId, HttpContext context, CancellationToken cancellationToken) =>
        {
            var record = await handlers.GetVideoAsync(id, cancellationToken);
            if (record is null) return Results.NotFound();
            return await MediaAssetResults.OpenAsync(agents, context, agentId, IndexApiHandlers.NormalizeAgentPath(record.Path));
        });
        app.MapGet("/api/gateway/v1/video-keyframes/{id:long}", async (long id, string? agentId, CancellationToken cancellationToken) =>
        {
            var path = handlers.GetVideoKeyframePath(id);
            if (path is null) return Results.NotFound();
            var asset = await agents.RequestAsync(agentId, IndexApiHandlers.NormalizeAgentPath(path), cancellationToken);
            return asset is null
                ? Results.Json(new { error = "media_agent_offline", detail = "没有在线的原图代理" }, statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.File(asset.Value.Content, "image/jpeg");
        });
        // Ordinal fallback keeps older mirrored rows readable when a client
        // carries a frame id from a previous service database snapshot.
        app.MapGet("/api/gateway/v1/video-keyframes/by-video/{videoId:long}/{ordinal:int}", async (long videoId, int ordinal, string? agentId, CancellationToken cancellationToken) =>
        {
            var path = handlers.GetVideoKeyframePathByOrdinal(videoId, ordinal);
            if (path is null) return Results.NotFound();
            var asset = await agents.RequestAsync(agentId, IndexApiHandlers.NormalizeAgentPath(path), cancellationToken);
            return asset is null
                ? Results.Json(new { error = "media_agent_offline" }, statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.File(asset.Value.Content, "image/jpeg");
        });
        // Media operations are executed by the source-machine agent.  The
        // client supplies only a video id; the service resolves the indexed
        // path and never accepts an arbitrary executable/path from the GUI.
        app.MapPost("/api/service/v1/index-repair", async (Picer.Core.Indexing.IndexRepairOperation operation, string? agentId, CancellationToken cancellationToken) =>
        {
            using var connection = db.OpenIndependentConnection();
            if (!Picer.Core.Indexing.IndexRepairService.MatchesCurrent(connection, operation))
                return Results.Conflict(new { error = "index_changed_since_scan" });
            try
            {
                var payload = await agents.RequestTaskAsync(agentId, "index.repair", JsonSerializer.Serialize(operation), cancellationToken);
                if (payload is null) return Results.Json(new { error = "media_agent_offline" }, statusCode: 503);
                var evidence = JsonSerializer.Deserialize<Picer.Core.Indexing.IndexRepairEvidence>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (evidence is null) return Results.Json(new { error = "invalid_repair_evidence" }, statusCode: 502);
                return Results.Json(Picer.Core.Indexing.IndexRepairService.Apply(connection, operation, evidence));
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
            { return Results.Json(new { error = "index_repair_failed", detail = exception.GetType().Name }, statusCode: 502); }
        });
        app.MapPost("/api/service/v1/directory-media", async (Picer.Core.Indexing.DirectoryMediaScanRequest request, string? agentId, CancellationToken cancellationToken) =>
        {
            using var connection = db.OpenIndependentConnection();
            var source = connection.GetSources().FirstOrDefault(x => x.Id == request.SourceId);
            if (source is null || !Picer.Core.Indexing.DirectoryMediaImport.Contains(source.Path, request.Directory))
                return Results.BadRequest(new { error = "directory_outside_source" });
            var directory = IndexApiHandlers.NormalizeAgentPath(request.Directory);
            try
            {
                var known = request.Snapshot is null && !request.ForceFullScan
                    ? Picer.Core.Indexing.DirectoryMediaImport.KnownEntries(connection, source.Id, directory) : null;
                var payload = await agents.RequestTaskAsync(agentId, "directory.media", JsonSerializer.Serialize(request with { Directory = directory, KnownEntries = known }), cancellationToken);
                if (payload is null) return Results.Json(new { error = "media_agent_offline" }, statusCode: 503);
                var page = JsonSerializer.Deserialize<Picer.Core.Indexing.DirectoryMediaPage>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (page is null) return Results.Json(new { error = "invalid_directory_page" }, statusCode: 502);
                page = page with { IndexChanged = Picer.Core.Indexing.DirectoryMediaImport.ApplyAndDetectChanges(connection, source.Id, directory, page) };
                return Results.Json(page);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
            {
                return Results.Json(new { error = "directory_scan_failed", detail = exception.GetType().Name }, statusCode: 502);
            }
        });
        app.MapGet("/api/service/v1/video-probe/{id:long}", async (long id, string? agentId, CancellationToken cancellationToken) =>
        {
            var record = await handlers.GetVideoAsync(id, cancellationToken);
            if (record is null) return Results.NotFound();
            try
            {
                var payload = await agents.RequestTaskAsync(agentId, "ffmpeg.probe", IndexApiHandlers.NormalizeAgentPath(record.Path), cancellationToken);
                if (!handlers.IsCurrentVideo(record)) return Results.Conflict(new { error = "video_content_changed" });
                return payload is null
                    ? Results.Json(new { error = "media_agent_offline" }, statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.Content(payload, "application/json");
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException)
            {
                return Results.Json(new { error = "media_task_failed", detail = exception.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });
        app.MapGet("/api/service/v1/video-audio/{id:long}", async (long id, string? agentId, CancellationToken cancellationToken) =>
        {
            var record = await handlers.GetVideoAsync(id, cancellationToken);
            if (record is null) return Results.NotFound();
            try
            {
                var payload = await agents.RequestTaskAsync(agentId, "ffmpeg.audio_extract", IndexApiHandlers.NormalizeAgentPath(record.Path), cancellationToken);
                if (!handlers.IsCurrentVideo(record)) return Results.Conflict(new { error = "video_content_changed" });
                if (string.IsNullOrWhiteSpace(payload)) return Results.Json(new { error = "media_agent_offline" }, statusCode: StatusCodes.Status503ServiceUnavailable);
                using var document = JsonDocument.Parse(payload);
                var content = Convert.FromBase64String(document.RootElement.GetProperty("contentBase64").GetString() ?? "");
                if (content.Length > 32L * 1024 * 1024) return Results.BadRequest(new { error = "audio_too_large" });
                return Results.File(content, document.RootElement.GetProperty("contentType").GetString() ?? "audio/wav", enableRangeProcessing: true);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or JsonException or FormatException)
            {
                return Results.Json(new { error = "media_task_failed", detail = exception.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });
        app.MapPost("/api/service/v1/video-keyframes/{id:long}", async (long id, string? agentId, CancellationToken cancellationToken) =>
        {
            var record = await handlers.GetVideoAsync(id, cancellationToken);
            if (record is null) return Results.NotFound();
            try
            {
                var payload = await agents.RequestTaskAsync(agentId, "ffmpeg.keyframes", IndexApiHandlers.NormalizeAgentPath(record.Path), cancellationToken);
                if (!handlers.IsCurrentVideo(record)) return Results.Conflict(new { error = "video_content_changed" });
                if (payload is null) return Results.Json(new { error = "media_agent_offline" }, statusCode: StatusCodes.Status503ServiceUnavailable);
                var frames = JsonSerializer.Deserialize<List<VideoKeyframe>>(payload,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
                var saved = handlers.SaveGeneratedKeyframes(id, frames, record);
                return Results.Json(saved);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or JsonException)
            {
                return Results.Json(new { error = "keyframe_task_failed", detail = exception.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });
        app.MapPost("/api/service/v1/video-transcribe/{id:long}", async (long id, string? agentId, CancellationToken cancellationToken) =>
        {
            var record = await handlers.GetVideoAsync(id, cancellationToken);
            if (record is null) return Results.NotFound();
            try
            {
                var payload = await agents.RequestTaskAsync(agentId, "asr.transcribe", IndexApiHandlers.NormalizeAgentPath(record.Path), cancellationToken);
                if (!handlers.IsCurrentVideo(record)) return Results.Conflict(new { error = "video_content_changed" });
                return payload is null
                    ? Results.Json(new { error = "media_agent_offline" }, statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.Content(payload, "application/json");
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or JsonException)
            {
                return Results.Json(new { error = "asr_task_failed", detail = exception.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });
        app.MapPost("/api/service/v1/asr", async (HttpRequest request, string? agentId, CancellationToken cancellationToken) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "multipart_audio_required" });
            var form = await request.ReadFormAsync(cancellationToken); var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0 || file.Length > 32L * 1024 * 1024) return Results.BadRequest(new { error = "audio_invalid" });
            await using var stream = file.OpenReadStream(); using var buffer = new MemoryStream(); await stream.CopyToAsync(buffer, cancellationToken);
            var payload = JsonSerializer.Serialize(new { audioBase64 = Convert.ToBase64String(buffer.ToArray()) });
            try
            {
                var result = await agents.RequestTaskAsync(agentId, "asr.stream", payload, cancellationToken);
                return result is null ? Results.Json(new { error = "media_agent_offline" }, statusCode: 503) : Results.Content(result, "application/json");
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException)
            { return Results.Json(new { error = "asr_stream_failed", detail = exception.Message }, statusCode: 502); }
        });
        app.MapPost("/api/service/v1/tts", async (HttpRequest request, string? agentId, CancellationToken cancellationToken) =>
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("text", out var text) || string.IsNullOrWhiteSpace(text.GetString()))
                return Results.BadRequest(new { error = "text_required" });
            try
            {
                var payload = await agents.RequestTaskAsync(agentId, "tts.synthesize", document.RootElement.GetRawText(), cancellationToken);
                return payload is null
                    ? Results.Json(new { error = "media_agent_offline" }, statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.Content(payload, "application/json");
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or JsonException)
            {
                return Results.Json(new { error = "tts_task_failed", detail = exception.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });
        app.MapPost("/api/service/v1/tts/stream", async (HttpRequest request, HttpResponse response, string? agentId, CancellationToken cancellationToken) =>
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            response.StatusCode = StatusCodes.Status200OK; response.ContentType = "audio/wav"; response.Headers.CacheControl = "no-cache";
            try
            {
                await agents.StreamTaskAsync(agentId, "tts.synthesize", document.RootElement.GetRawText(), async chunk =>
                {
                    await response.Body.WriteAsync(chunk, cancellationToken); await response.Body.FlushAsync(cancellationToken);
                }, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException)
            {
                if (!response.HasStarted) response.StatusCode = StatusCodes.Status502BadGateway;
            }
        });
        app.MapPost("/api/service/v1/vl", async (HttpRequest request, string? agentId, CancellationToken cancellationToken) =>
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            try
            {
                var payload = await agents.RequestTaskAsync(agentId, "vl.analyze", document.RootElement.GetRawText(), cancellationToken);
                return payload is null
                    ? Results.Json(new { error = "media_agent_offline" }, statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.Content(payload, "application/json");
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or JsonException)
            {
                return Results.Json(new { error = "vl_task_failed", detail = exception.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });
        app.MapPost("/api/service/v1/vl/stream", async (HttpRequest request, HttpResponse response, string? agentId, CancellationToken cancellationToken) =>
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            response.StatusCode = StatusCodes.Status200OK; response.ContentType = "text/event-stream"; response.Headers.CacheControl = "no-cache";
            try
            {
                var requestPayload = JsonNode.Parse(document.RootElement.GetRawText())?.AsObject()
                    ?? throw new JsonException("vl_request_object_required");
                requestPayload["stream"] = true;
                await agents.StreamTaskAsync(agentId, "vl.analyze", requestPayload.ToJsonString(), async chunk =>
                {
                    await response.Body.WriteAsync(chunk, cancellationToken); await response.Body.FlushAsync(cancellationToken);
                }, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException)
            {
                if (!response.HasStarted) response.StatusCode = StatusCodes.Status502BadGateway;
            }
        });
        app.MapMethods("/api/service/v1/training/api/face-training/{**path}", ["GET", "POST"],
            async (string path, HttpRequest request, string? agentId, CancellationToken cancellationToken) =>
        {
            var target = path + request.QueryString.Value;
            if (!Picer.Core.People.TrainingRelayContract.IsAllowed(request.Method, target)) return Results.NotFound();
            using var reader = new StreamReader(request.Body);
            var body = request.Method == "POST" ? await reader.ReadToEndAsync(cancellationToken) : null;
            if (body?.Length > 16 * 1024 * 1024) return Results.StatusCode(413);
            var envelope = new Picer.Core.People.TrainingRelayRequest(request.Method, target, body);
            try
            {
                var payload = await agents.RequestTaskAsync(agentId, "training.proxy", JsonSerializer.Serialize(envelope), cancellationToken);
                if (payload is null) return Results.StatusCode(503);
                var result = JsonSerializer.Deserialize<Picer.Core.People.TrainingRelayResponse>(payload);
                if (result is null || result.StatusCode is < 200 or > 599) return Results.StatusCode(502);
                return Results.Content(result.Body, "application/json", statusCode: result.StatusCode);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or JsonException)
            { return Results.Json(new { error = "training_relay_failed" }, statusCode: 502); }
        });
        app.MapPost("/api/service/v1/face/{operation}", async (string operation, HttpRequest request, string? agentId, CancellationToken cancellationToken) =>
        {
            if (operation is not ("detect" or "embed" or "calibrate" or "train")) return Results.NotFound();
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            try
            {
                var payload = await agents.RequestTaskAsync(agentId, "face." + operation, document.RootElement.GetRawText(), cancellationToken);
                if (payload is null) return Results.Json(new { error = "media_agent_offline" }, statusCode: StatusCodes.Status503ServiceUnavailable);
                // MediaAgent implementations use the wire-format camelCase
                // contract.  Use web defaults here so a valid remote result
                // is not rejected merely because the server DTO is PascalCase.
                var result = JsonSerializer.Deserialize<FaceServiceResult>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (!FaceServiceContract.IsValid(result, operation)) return Results.Json(new { error = "face_result_schema_invalid", schema = FaceServiceContract.CurrentSchema }, statusCode: StatusCodes.Status502BadGateway);
                if (document.RootElement.TryGetProperty("modelVersion", out var requestedModelElement) &&
                    requestedModelElement.ValueKind == JsonValueKind.String &&
                    !string.Equals(result!.ModelVersion, requestedModelElement.GetString(), StringComparison.OrdinalIgnoreCase))
                    return Results.Json(new { error = "face_model_mismatch", expected = requestedModelElement.GetString(), actual = result.ModelVersion }, statusCode: StatusCodes.Status409Conflict);
                if (document.RootElement.TryGetProperty("imageId", out var imageIdElement) && imageIdElement.TryGetInt64(out var imageId) && operation is "detect" or "embed")
                {
                    using var transaction = db.BeginTransaction();
                    FaceDetectionReconciler.Save(db, imageId, result!.ModelVersion,
                        result.Faces.Select(face => new FaceDetection(new NormalizedFaceBox(face.X, face.Y, face.Width, face.Height),
                            face.Confidence, Embedding: face.Embedding)).ToArray(), transaction);
                    transaction.Commit();
                }
                return Results.Json(result);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or JsonException)
            {
                return Results.Json(new { error = "face_task_failed", operation, detail = exception.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });
        app.MapPost("/api/service/v1/video-people/{id:long}", async (long id, string? agentId, CancellationToken cancellationToken) =>
        {
            var record = await handlers.GetVideoAsync(id, cancellationToken);
            if (record is null) return Results.NotFound();
            try
            {
                // Send the mirrored keyframe manifest to the source-host agent.  A
                // video itself is not an image and must never be sent directly to
                // the face adapter; preserving the service keyframe IDs also makes
                // the returned detections joinable with video_detected_faces.
                var keyframes = handlers.GetVideoKeyframes(id)
                    .Select(frame => new
                    {
                        id = frame.Id,
                        timestampSeconds = frame.TimestampSeconds,
                        path = IndexApiHandlers.NormalizeAgentPath(frame.Path)
                    }).ToArray();
                if (keyframes.Length == 0)
                    return Results.Conflict(new { error = "video_people_keyframes_required" });
                AppLogger.Info($"Remote video people analysis started; videoId={id}; keyframes={keyframes.Length}; agentSpecified={!string.IsNullOrWhiteSpace(agentId)}");
                var payload = JsonSerializer.Serialize(new
                {
                    videoId = id,
                    modelVersion = "insightface-arcface",
                    modelPriority = new[] { "insightface-arcface", "adaface", "arcface-paddle", "facex-zoo", "arcface", "sface" },
                    path = IndexApiHandlers.NormalizeAgentPath(record.Path),
                    keyframes
                });
                var result = await agents.RequestTaskAsync(agentId, "video.people", payload, cancellationToken);
                if (result is null) return Results.Json(new { error = "media_agent_offline" }, statusCode: StatusCodes.Status503ServiceUnavailable);
                var faceResult = JsonSerializer.Deserialize<FaceServiceResult>(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (!FaceServiceContract.IsValid(faceResult, "video.people")) return Results.Json(new { error = "face_result_schema_invalid", schema = FaceServiceContract.CurrentSchema }, statusCode: StatusCodes.Status502BadGateway);
                var allowedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "insightface-arcface", "adaface", "arcface-paddle", "facex-zoo", "arcface", "sface" };
                if (!allowedModels.Contains(faceResult!.ModelVersion))
                {
                    AppLogger.Warn($"Remote video people analysis rejected; videoId={id}; reason=face_model_mismatch; allowedModels={string.Join(',', allowedModels)}; actual={faceResult.ModelVersion}");
                    return Results.Json(new { error = "face_model_mismatch", expected = "one_of_prioritized_face_models", actual = faceResult.ModelVersion }, statusCode: StatusCodes.Status409Conflict);
                }
                if (!string.IsNullOrWhiteSpace(faceResult.Error))
                    return Results.Json(new { error = "video_face_analysis_incomplete", detail = SafeVideoPeopleDiagnostic(faceResult.Error) }, statusCode: StatusCodes.Status502BadGateway);
                if (faceResult!.Faces.Any(face => face.KeyframeId is null)) return Results.Json(new { error = "video_face_keyframe_id_required" }, statusCode: StatusCodes.Status502BadGateway);
                var requestedKeyframeIds = keyframes.Select(frame => frame.id).ToHashSet();
                if (faceResult.Faces.Any(face => !requestedKeyframeIds.Contains(face.KeyframeId!.Value)))
                    return Results.Json(new { error = "video_face_keyframe_id_unknown" }, statusCode: StatusCodes.Status502BadGateway);
                var matcher = new FaceSuggestionEngine(new VisionAnalysisRepository(db), new PeopleService(db))
                    .CreateMatcher(faceResult.ModelVersion);
                using var transaction = db.BeginTransaction();
                if (!new VideoRepository(db).IsCurrentContent(id, record)) return Results.Conflict(new { error = "video_content_changed" });
                foreach (var face in faceResult.Faces)
                {
                    // Do not rely on a legacy UNIQUE(keyframe_id,ordinal)
                    // constraint: older mirrored databases legitimately lack
                    // that auto-index.  Update first to preserve manual person
                    // assignments, then insert only when the row is new.
                    var suggestion = matcher.Match(face.Embedding, face.Confidence, face.Width * face.Height);
                    using var update = db.Command("UPDATE video_detected_faces SET video_id=$video,timestamp_seconds=$time,model_version=$model,box_x=$x,box_y=$y,box_width=$w,box_height=$h,confidence=$confidence,embedding=$embedding,suggested_person_id=CASE WHEN assignment_state IS NULL THEN $person ELSE suggested_person_id END,match_confidence=CASE WHEN assignment_state IS NULL THEN $match ELSE match_confidence END WHERE keyframe_id=$keyframe AND ordinal=$ordinal AND model_version=$model");
                    update.Transaction = transaction;
                    update.Parameters.AddWithValue("$video", new VideoContentRepository(db).GetContentId(id)); update.Parameters.AddWithValue("$keyframe", face.KeyframeId!.Value); update.Parameters.AddWithValue("$time", face.TimestampSeconds ?? 0); update.Parameters.AddWithValue("$model", faceResult.ModelVersion); update.Parameters.AddWithValue("$x", face.X); update.Parameters.AddWithValue("$y", face.Y); update.Parameters.AddWithValue("$w", face.Width); update.Parameters.AddWithValue("$h", face.Height); update.Parameters.AddWithValue("$confidence", face.Confidence); update.Parameters.AddWithValue("$embedding", face.Embedding is null ? DBNull.Value : EmbeddingBytes(face.Embedding)); update.Parameters.AddWithValue("$person", suggestion.PersonId is null ? DBNull.Value : suggestion.PersonId.Value); update.Parameters.AddWithValue("$match", suggestion.Score is null ? DBNull.Value : suggestion.Score.Value); update.Parameters.AddWithValue("$ordinal", face.Ordinal);
                    if (update.ExecuteNonQuery() == 0)
                    {
                        using var insert = db.Command("INSERT INTO video_detected_faces(video_id,keyframe_id,timestamp_seconds,ordinal,model_version,box_x,box_y,box_width,box_height,confidence,embedding,suggested_person_id,match_confidence,created_utc) VALUES($video,$keyframe,$time,$ordinal,$model,$x,$y,$w,$h,$confidence,$embedding,$person,$match,$created)");
                        insert.Transaction = transaction;
                        insert.Parameters.AddWithValue("$video", new VideoContentRepository(db).GetContentId(id)); insert.Parameters.AddWithValue("$keyframe", face.KeyframeId.Value); insert.Parameters.AddWithValue("$time", face.TimestampSeconds ?? 0); insert.Parameters.AddWithValue("$ordinal", face.Ordinal); insert.Parameters.AddWithValue("$model", faceResult.ModelVersion); insert.Parameters.AddWithValue("$x", face.X); insert.Parameters.AddWithValue("$y", face.Y); insert.Parameters.AddWithValue("$w", face.Width); insert.Parameters.AddWithValue("$h", face.Height); insert.Parameters.AddWithValue("$confidence", face.Confidence); insert.Parameters.AddWithValue("$embedding", face.Embedding is null ? DBNull.Value : EmbeddingBytes(face.Embedding)); insert.Parameters.AddWithValue("$person", suggestion.PersonId is null ? DBNull.Value : suggestion.PersonId.Value); insert.Parameters.AddWithValue("$match", suggestion.Score is null ? DBNull.Value : suggestion.Score.Value); insert.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("o")); insert.ExecuteNonQuery();
                    }
                }
                using (var updateStatus = db.Command("UPDATE videos SET person_analysis_status='completed',person_analyzed_utc=$now WHERE content_id=(SELECT content_id FROM videos WHERE id=$video)"))
                {
                    updateStatus.Transaction = transaction;
                    updateStatus.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
                    updateStatus.Parameters.AddWithValue("$video", id);
                    updateStatus.ExecuteNonQuery();
                }
                transaction.Commit();
                AppLogger.Info($"Remote video people analysis indexed; videoId={id}; faces={faceResult.Faces.Count}; model={faceResult.ModelVersion}; fallbackUsed={!faceResult.ModelVersion.Equals("insightface-arcface", StringComparison.OrdinalIgnoreCase)}");
                return Results.Json(faceResult);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or JsonException)
            {
                var detail = SafeVideoPeopleDiagnostic(exception.Message);
                AppLogger.Error($"Remote video people analysis failed; videoId={id}; diagnostic={detail}; exceptionType={exception.GetType().Name}");
                return Results.Json(new { error = "video_people_task_failed", detail }, statusCode: StatusCodes.Status502BadGateway);
            }
        });
        app.MapPost("/api/ai/v1/chat/stream", async (HttpRequest request, HttpResponse response, string? agentId, CancellationToken cancellationToken) =>
        {
            var requestId = request.Headers["X-Picer-Request-Id"].ToString();
            if (string.IsNullOrWhiteSpace(requestId)) requestId = Guid.NewGuid().ToString("N");
            var started = Stopwatch.StartNew();
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, cancellationToken);
            AppLogger.Info($"AI proxy accepted; requestId={requestId}; agentSpecified={!string.IsNullOrWhiteSpace(agentId)}; onlineAgents={agents.OnlineCount}; payloadBytes={buffer.Length}");
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache";
            response.Headers["X-Accel-Buffering"] = "no";
            try
            {
                await agents.StreamAiAsync(agentId, buffer.ToArray(), async chunk =>
                {
                    await response.Body.WriteAsync(chunk, cancellationToken);
                    await response.Body.FlushAsync(cancellationToken);
                }, cancellationToken);
                AppLogger.Info($"AI proxy completed; requestId={requestId}; elapsedMs={started.ElapsedMilliseconds}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                AppLogger.Warn($"AI stream proxy failed; requestId={requestId}; elapsedMs={started.ElapsedMilliseconds}; onlineAgents={agents.OnlineCount}; exception={exception.GetType().Name}; inner={(exception.InnerException?.GetType().Name ?? "none")}; message={exception.Message}");
                if (!response.HasStarted)
                {
                    response.Clear();
                    response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    response.ContentType = "application/json; charset=utf-8";
                    await response.WriteAsJsonAsync(new { error = "ai_proxy_unavailable", detail = exception.Message }, CancellationToken.None);
                }
                else
                {
                    var error = JsonSerializer.Serialize(new { error = "ai_proxy_failed" });
                    await response.WriteAsync($"event: error\ndata: {error}\n\n", CancellationToken.None);
                }
            }
        });
        app.MapPost("/api/gateway/v1/search", async (GatewaySearchRequest request, CancellationToken cancellationToken) =>
            Results.Json(await handlers.SearchAsync(request, cancellationToken)));
        app.MapGet("/api/gateway/v1/sources", () => Results.Json(handlers.ListSources()));
        app.MapPost("/api/gateway/v1/changes", (GatewayChangeRequest request) =>
        {
            try { return Results.Json(handlers.GetChanges(request)); }
            catch (ArgumentOutOfRangeException) { return Results.BadRequest(new { error = "invalid_change_checkpoint" }); }
        });
        app.MapGet("/api/gateway/v1/tags", () => Results.Json(handlers.ListTags()));
        app.MapGet("/api/gateway/v1/albums", () => Results.Json(handlers.ListAlbums()));
        app.MapGet("/api/gateway/v1/people", () => Results.Json(handlers.ListPeople()));
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
        app.MapPost("/api/gateway/v1/videos/{id:long}/purge", async (long id, CancellationToken cancellationToken) =>
        {
            var result = await handlers.PurgeVideoAsync(id, cancellationToken);
            mediaPreviews.Clear();
            await agents.ClearMediaCachesAsync(cancellationToken);
            return Results.Json(new PipeFlagResponse(result));
        });
        app.MapPost("/api/gateway/v1/images/{id:long}/purge-missing", async (long id, CancellationToken cancellationToken) =>
        {
            var result = handlers.PurgeMissingImage(id);
            mediaPreviews.Clear();
            await agents.ClearMediaCachesAsync(cancellationToken);
            return Results.Json(new PipeFlagResponse(result));
        });

        app.Lifetime.ApplicationStopped.Register(() =>
        {
            AppLogger.Info("Picer.IndexService 已停止");
            db.Dispose();
        });
        return new IndexServiceInstance(app, certificate, handlers, engine, jobs);
    }

    private static byte[] EmbeddingBytes(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static string SafeVideoPeopleDiagnostic(string? value)
    {
        var safeCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "face_endpoint_not_configured", "face_service_unavailable", "face_request_failed",
            "video_people_keyframes_required", "video_people_requires_insightface_model",
            "video_people_no_faces", "media_agent_offline", "task_payload_required",
            "task_not_supported", "task_payload_too_large", "media_agent_disconnected"
        };
        if (string.IsNullOrWhiteSpace(value)) return "video_analysis_failed";
        var candidate = value.Trim();
        if (safeCodes.Contains(candidate)) return candidate;
        var separator = candidate.IndexOf(':');
        if (separator > 0 && safeCodes.Contains(candidate[..separator])) return candidate[..separator];
        return "video_analysis_failed";
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
