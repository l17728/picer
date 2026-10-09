using System.IO;
using System.Net.Http;
using Microsoft.ML.OnnxRuntime;
using Picer.App.FileOperations;
using Picer.Core.Security;
using Picer.Core.Data;
using Picer.Core.FileOperations;
using Picer.Core.Gateway;
using Picer.Core.Indexing;
using Picer.Core.Geocoding;
using Picer.Core.Logging;
using Picer.Core.People;
using Picer.Core.Storage;
using Picer.Core.Thumbnails;
using Picer.Core.Vision;
using Picer.Core.Video;
using Picer.Core.SemanticSearch;
using Picer.Core.Search;
using Picer.App.Infrastructure;

namespace Picer.App;

public static partial class AppServices
{
    private static readonly SemaphoreSlim RemoteMirrorGate = new(1, 1);
    private static bool _remoteMediaBootstrapped;
    private static int _remoteSyncRequested;
    private static int _remoteSyncWorkerRunning;
    public static IIndexGateway Gateway { get; private set; } = null!;
    private static IIndexGateway? SyncGateway { get; set; }
    public static IndexSyncClient? SyncClient { get; private set; }
    public static bool ServiceMode { get; private set; }
    public static PicerDatabase Db { get; private set; } = null!;
    public static IStorageProvider Storage { get; private set; } = null!;
    public static ThumbnailService Thumbnails { get; private set; } = null!;
    public static RemoteThumbnailCache RemoteThumbnails { get; private set; } = null!;
    public static IndexEngine Index { get; private set; } = null!;
    public static IUserAuthorizedFileOperation FileOperations { get; private set; } = null!;
    public static IReverseGeocoder Geocoder { get; private set; } = null!;
    public static LocationHierarchyService LocationHierarchy { get; private set; } = null!;
    public static StorageConfig Config { get; private set; } = null!;
    public static VisionAnalysisRepository VisionRepository { get; private set; } = null!;
    public static PeopleService People { get; private set; } = null!;
    public static VisionAnalysisService? VisionAnalysis { get; private set; }
    public static string? VisionUnavailableReason { get; private set; }
    public static FaceThumbnailService FaceThumbnails { get; private set; } = null!;
    public static FaceSuggestionEngine FaceSuggestions { get; private set; } = null!;
    public static UnknownFaceClusterer UnknownFaceClusters { get; private set; } = null!;
    public static FaceValidationService FaceValidation { get; private set; } = null!;
    public static FaceTrainingService FaceTraining { get; private set; } = null!;
    public static CloudVisionRepository CloudVisionRepository { get; private set; } = null!;
    public static MediaSearchTool MediaSearch { get; private set; } = null!;
    public static VisualConceptRepository VisualConceptRepository { get; private set; } = null!;
    public static VideoRepository Videos { get; private set; } = null!;
    public static VideoIndexService VideoIndex { get; private set; } = null!;
    public static IVideoMediaProcessor? VideoKeyframes { get; private set; }
    public static IVideoTranscriptionService? VideoTranscription { get; private set; }
    public static VideoFaceTimelineService? VideoFaceTimeline { get; private set; }
    public static VideoSummaryService VideoSummaries { get; private set; } = null!;
    public static ClipSemanticRepository ClipRepository { get; private set; } = null!;
    public static FlorenceResultRepository FlorenceResults { get; private set; } = null!;
    public static ClipSemanticService? ClipSemantic { get; private set; }
    public static string? ClipUnavailableReason { get; private set; }
    private static ChineseClipEncoder? ClipEncoder { get; set; }
    private static IImageFaceAnalyzer? FaceAnalyzer { get; set; }

    private static string ConfigPath => Path.Combine(AppPaths.DataRoot, "storage.json");

    public static void Initialize()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PICER_SCAN_CACHE_ROOT")))
            Environment.SetEnvironmentVariable("PICER_SCAN_CACHE_ROOT", Path.Combine(AppPaths.DataRoot, "scan-manifests"));
        AppLogger.Initialize(Path.Combine(AppPaths.DataRoot, "logs"));
        AppLogger.Info("AppServices 初始化开始");

        var backend = AppPaths.LoadIndexBackend();
        ServiceMode = backend.Mode == IndexBackendConfig.ServiceMode;
        if (ServiceMode && backend.BaseUrl is { Length: > 0 } serviceUrl)
            SyncClient = IndexSyncClient.Create(serviceUrl, RequireToken(backend), backend.SpkiPin);
        if (ServiceMode)
        {
            Gateway = backend.BaseUrl is { Length: > 0 } baseUrl
                ? new HttpIndexGateway(baseUrl, RequireToken(backend), backend.SpkiPin)
                : new PipeIndexGateway(backend.PipeName ?? throw new InvalidOperationException("service-endpoint.json 缺少管道名与 URL。"), RequireToken(backend));
            AppLogger.Info("AppServices 已进入 service 模式（零构建逻辑，仅网关）");
            Db = new PicerDatabase(Path.Combine(AppPaths.DataRoot, "remote-mirror.db"));
            Config = StorageConfig.Load(ConfigPath);
            Storage = Config.CreateProvider(Path.Combine(AppPaths.DataRoot, "remote-cache"));
            Thumbnails = new ThumbnailService(Path.Combine(AppPaths.DataRoot, "remote-thumbs"));
            RemoteThumbnails = new RemoteThumbnailCache(Path.Combine(AppPaths.DataRoot, "remote-thumbs"));
            Index = new IndexEngine(Db, Storage, Thumbnails);
            FileOperations = new WindowsRecycleBinFileOperation();
            Geocoder = new NominatimReverseGeocoder(Path.Combine(AppPaths.DataRoot, "remote-geocoding-cache.json"));
            LocationHierarchy = new LocationHierarchyService(Db, Geocoder);
            VisionRepository = new VisionAnalysisRepository(Db);
            People = new PeopleService(Db);
            FaceThumbnails = new FaceThumbnailService(Path.Combine(AppPaths.DataRoot, "remote-face-thumbs"));
            FaceSuggestions = new FaceSuggestionEngine(VisionRepository, People);
            UnknownFaceClusters = new UnknownFaceClusterer(VisionRepository);
            FaceValidation = new FaceValidationService(Db, VisionRepository);
            FaceTraining = new FaceTrainingService(VisionRepository, clientFactory: CreateTrainingHttpClient);
            CloudVisionRepository = new CloudVisionRepository(Db);
            VisualConceptRepository = new VisualConceptRepository(Db);
            Videos = new VideoRepository(Db);
            MediaSearch = new MediaSearchTool(Db, Videos);
            VideoIndex = new VideoIndexService(Db, Videos);
            VideoSummaries = new VideoSummaryService(Videos, Path.Combine(AppPaths.DataRoot, "remote-video-summary-cache"),
                (work, token) => Infrastructure.UiWork.RunDatabaseAsync(work, token));
            ClipRepository = new ClipSemanticRepository(Db);
            FlorenceResults = new FlorenceResultRepository(Db);
            // Remote mode still needs a read-only timeline analyzer so the
            // mirrored video faces are shown. No face inference runs here;
            // the service analyzer is only used for its model identity while
            // VideoFaceTimelineService reads synchronized face rows.
            try
            {
                if (backend.BaseUrl is not { Length: > 0 } remoteFaceUrl
                    || !Uri.TryCreate(remoteFaceUrl.TrimEnd('/') + "/", UriKind.Absolute, out var remoteFaceEndpoint))
                    throw new InvalidOperationException("IndexService face endpoint is not configured");
                FaceAnalyzer = new ServiceFaceAnalyzer(remoteFaceEndpoint, RequireToken(backend), backend.SpkiPin, "insightface-arcface");
                VideoFaceTimeline = new VideoFaceTimelineService(Db, FaceAnalyzer, VisionRepository, People, includeAllModels: false);
                AppLogger.Info("remote mode video face timeline initialized from mirrored metadata");
            }
            catch (Exception exception) when (exception is InvalidOperationException or UriFormatException)
            {
                VideoFaceTimeline = null;
                AppLogger.Warn("remote mode video face timeline unavailable: " + exception.Message);
            }
            // The WPF shell starts exactly one mirror bootstrap after it is
            // visible. Starting another pull here raced that bootstrap against
            // People/Video queries on the same SQLite connection and could
            // leave panels stuck on their loading placeholder.
            return;
        }

        Db = new PicerDatabase(AppPaths.DbPath);

        // Local mode remains offline by default. When an IndexService endpoint
        // is explicitly supplied, publish local mutations in the background
        // without changing the local gateway used by the UI.
        var syncIp = Environment.GetEnvironmentVariable("PICER_INDEX_SYNC_IP");
        var syncToken = Environment.GetEnvironmentVariable("PICER_INDEX_SERVICE_TOKEN");
        if (!string.IsNullOrWhiteSpace(syncIp) && !string.IsNullOrWhiteSpace(syncToken)
            && System.Net.IPAddress.TryParse(syncIp, out _))
        {
            SyncClient = IndexSyncClient.Create($"https://{syncIp}:8445", syncToken);
            SyncGateway = new HttpIndexGateway($"https://{syncIp}:8445", syncToken);
            AppLogger.Info("local mode incremental index publisher configured from PICER_INDEX_SYNC_IP");
        }
        else
        {
            try
            {
                var paired = IndexBackendConfig.LoadConfiguredServiceEndpoint(
                    AppPaths.ServiceEndpointPath,
                    static cipher => System.Security.Cryptography.ProtectedData.Unprotect(cipher, null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
                if (paired?.BaseUrl is { Length: > 0 } pairedUrl && paired.Token is { Length: > 0 } pairedToken)
                {
                    SyncClient = IndexSyncClient.Create(pairedUrl, pairedToken, paired.SpkiPin);
                    SyncGateway = new HttpIndexGateway(pairedUrl, pairedToken, paired.SpkiPin);
                    AppLogger.Info("local mode incremental index publisher configured from paired service endpoint");
                }
            }
            catch (Exception exception)
            {
                AppLogger.Warn("local mode paired sync endpoint unavailable: " + exception.Message);
            }
        }

        var credentials = new WindowsCredentialStore();
        var legacyWebDavPassword = StorageConfig.ReadLegacyWebDavPassword(ConfigPath);
        if (!string.IsNullOrEmpty(legacyWebDavPassword)) credentials.Write(PicerCredentialTargets.WebDav, legacyWebDavPassword);
        Config = StorageConfig.Load(ConfigPath);
        if (!string.IsNullOrEmpty(legacyWebDavPassword)) Config.Save(ConfigPath);
        var cacheDir = ResolveCacheDirectory(Config);
        Storage = Config.CreateProvider(cacheDir, credentials.Read(PicerCredentialTargets.WebDav));

        Thumbnails = new ThumbnailService(AppPaths.ThumbnailDir);
        Index = new IndexEngine(Db, Storage, Thumbnails);
        FileOperations = new WindowsRecycleBinFileOperation();
        Geocoder = new NominatimReverseGeocoder(Path.Combine(AppPaths.DataRoot, "geocoding-cache.json"));
        LocationHierarchy = new LocationHierarchyService(Db, Geocoder);
        VisionRepository = new VisionAnalysisRepository(Db);
        People = new PeopleService(Db);
        FaceThumbnails = new FaceThumbnailService(AppPaths.FaceThumbnailDir);
        FaceSuggestions = new FaceSuggestionEngine(VisionRepository, People);
        UnknownFaceClusters = new UnknownFaceClusterer(VisionRepository);
        FaceValidation = new FaceValidationService(Db, VisionRepository);
        FaceTraining = new FaceTrainingService(VisionRepository, clientFactory: CreateTrainingHttpClient);
        CloudVisionRepository = new CloudVisionRepository(Db);
        VisualConceptRepository = new VisualConceptRepository(Db);
        Videos = new VideoRepository(Db);
        MediaSearch = new MediaSearchTool(Db, Videos);
        VideoIndex = new VideoIndexService(Db, Videos);
        VideoSummaries = new VideoSummaryService(Videos, Path.Combine(AppPaths.DataRoot, "video-summary-cache"),
            (work, token) => Infrastructure.UiWork.RunDatabaseAsync(work, token));
        ClipRepository = new ClipSemanticRepository(Db);
        FlorenceResults = new FlorenceResultRepository(Db);
        if (TryGetAiServiceBackend(out var mediaBackend)
            && mediaBackend.BaseUrl is { Length: > 0 } mediaService
            && Uri.TryCreate(mediaService.TrimEnd('/') + "/", UriKind.Absolute, out var mediaServiceEndpoint))
        {
            VideoKeyframes = new ServiceVideoMediaProcessor(
                mediaServiceEndpoint, mediaBackend.Token ?? "", Path.Combine(AppPaths.DataRoot, "video-keyframes"));
            VideoTranscription = null; // resolved on demand by CreateVideoTranscriptionService
        }
        else
        {
            VideoKeyframes = null;
            VideoTranscription = null;
        }
        try
        {
            if (!TryGetAiServiceBackend(out var faceBackend)
                || faceBackend.BaseUrl is not { Length: > 0 } faceService
                || !Uri.TryCreate(faceService.TrimEnd('/') + "/", UriKind.Absolute, out var faceServiceEndpoint))
                throw new InvalidOperationException("IndexService face endpoint is not configured");
            FaceAnalyzer = new ServiceFaceAnalyzer(faceServiceEndpoint, faceBackend.Token ?? "", faceBackend.SpkiPin, "insightface-arcface");
            VisionAnalysis = new VisionAnalysisService(VisionRepository, FaceAnalyzer);
            VideoFaceTimeline = new VideoFaceTimelineService(Db, FaceAnalyzer, VisionRepository, People, includeAllModels: false);
            VisionUnavailableReason = null;
            AppLogger.Info("face analysis routed through IndexService; local GUI does not load ONNX weights");
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
        {
            VisionAnalysis = null;
            VisionUnavailableReason = $"人脸模型尚未就绪：{ex.Message}";
            AppLogger.Info(VisionUnavailableReason);
        }
        AppLogger.Info("AppServices 初始化完成");
        Gateway = new LocalGateway(Db, Thumbnails, Index);
    }

    private static string RequireToken(IndexBackendOptions backend) =>
        backend.Token ?? throw new InvalidOperationException("service-endpoint.json 缺少访问令牌。");

    public static void Reinitialize(IStorageProvider provider)
    {
        Storage = provider;
        Index = new IndexEngine(Db, Storage, Thumbnails);
        if (!ServiceMode) Gateway = new LocalGateway(Db, Thumbnails, Index);
        GetClientServices();
    }

    public static string? GetWebDavPassword() => new WindowsCredentialStore().Read(PicerCredentialTargets.WebDav);

    public static void ApplyStorageConfig(StorageConfig config, string? password)
    {
        ArgumentNullException.ThrowIfNull(config);
        var credentials = new WindowsCredentialStore();
        if (!string.IsNullOrWhiteSpace(password)) credentials.Write(PicerCredentialTargets.WebDav, password);
        config.Save(ConfigPath);
        Config = config;
        Reinitialize(config.CreateProvider(ResolveCacheDirectory(config), credentials.Read(PicerCredentialTargets.WebDav)));
    }

    public static void DeleteWebDavPassword() => new WindowsCredentialStore().Delete(PicerCredentialTargets.WebDav);

    public static ClipSemanticService? EnsureClipSemantic()
    {
        if (ClipSemantic is not null) return ClipSemantic;
        try
        {
            throw new InvalidOperationException("IndexService CLIP endpoint is not registered");
        }
        catch (Exception ex) when (ex is FileNotFoundException or OnnxRuntimeException or InvalidOperationException)
        {
            ClipUnavailableReason = $"中文语义模型未就绪：{ex.Message}";
        }
        return ClipSemantic;
    }

    private static string ResolveCacheDirectory(StorageConfig config) =>
        config.Cache?.Directory is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : Path.Combine(AppPaths.DataRoot, "cache");

    public static async Task SyncRemoteMirrorAsync(CancellationToken cancellationToken = default, bool requireMetadataSync = false, bool refreshMedia = false)
    {
        await RemoteMirrorGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (refreshMedia) Volatile.Write(ref _remoteMediaBootstrapped, false);
            await SyncRemoteMirrorCoreAsync(cancellationToken, requireMetadataSync).ConfigureAwait(false);
        }
        finally { RemoteMirrorGate.Release(); }
    }

    private static async Task SyncRemoteMirrorCoreAsync(CancellationToken cancellationToken, bool requireMetadataSync)
    {
        var mirrorGateway = ServiceMode ? Gateway : SyncGateway;
        if (mirrorGateway is null)
        {
            if (requireMetadataSync) throw new InvalidOperationException("remote_mirror_gateway_unavailable");
            return;
        }
        if (SyncClient is not null)
        {
            try { await SyncClient.PushMetadataEditsAsync(Db, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
            { AppLogger.Warn("Manual metadata sync pending: " + exception.GetType().Name); }
            try { await SyncClient.PushFaceReviewDecisionsAsync(Db, cancellationToken).ConfigureAwait(false); }
            catch (HttpRequestException)
            {
                AppLogger.Warn("人工人脸状态同步未完成，保留本地操作日志并等待重试");
            }
        }
        var sources = await mirrorGateway.ListSourcesAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var preferences = SyncClient is not null
                ? await SyncClient.GetDirectoryClassificationAsync(cancellationToken).ConfigureAwait(false)
                : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (preferences.Count > 0)
                new DirectoryClassificationPreferences(AppPaths.DirectoryClassificationSettingsPath).Merge(preferences);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            AppLogger.Warn("Remote directory classification preferences unavailable: " + exception.Message);
        }
        var incremental = await RefreshChangedMediaAsync(mirrorGateway, sources, cancellationToken).ConfigureAwait(false);
        if (incremental.Supported && incremental.Checkpoint?.MetadataRevision == incremental.MetadataRevision)
        {
            Volatile.Write(ref _remoteMediaBootstrapped, true);
            return;
        }
        var metadataComplete = true;
        // Metadata routes were added after the initial index-service deployment. Keep
        // image/video mirroring compatible with an older service while it is being
        // upgraded; each collection is optional and can be retried on the next sync.
        try
        {
            var tags = await mirrorGateway.ListTagsAsync(cancellationToken).ConfigureAwait(false);
            await UiWork.RunDatabaseAsync("RemoteMirror.Tags", () =>
            {
                foreach (var tag in tags) Db.UpsertMirroredTag(tag);
                Db.ReconcileMirroredCatalog("tag", tags.Select(item => item.Id), ServiceMode && incremental.Supported, SyncClient?.AcknowledgedMetadataOperations);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            AppLogger.Warn("Remote tag metadata is unavailable; continuing media sync: " + ex.Message);
            metadataComplete = false;
        }
        try
        {
            var albums = await mirrorGateway.ListAlbumsAsync(cancellationToken).ConfigureAwait(false);
            await UiWork.RunDatabaseAsync("RemoteMirror.Albums", () =>
            {
                foreach (var album in albums) Db.UpsertMirroredAlbum(album);
                Db.ReconcileMirroredCatalog("album", albums.Select(item => item.Id), ServiceMode && incremental.Supported, SyncClient?.AcknowledgedMetadataOperations);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            AppLogger.Warn("Remote album metadata is unavailable; continuing media sync: " + ex.Message);
            metadataComplete = false;
        }
        try
        {
            var people = await mirrorGateway.ListPeopleAsync(cancellationToken).ConfigureAwait(false);
            await UiWork.RunDatabaseAsync("RemoteMirror.People", () =>
            {
                foreach (var person in people) Db.UpsertMirroredPerson(person);
                Db.ReconcileMirroredCatalog("person", people.Select(item => item.Id), ServiceMode && incremental.Supported, SyncClient?.AcknowledgedMetadataOperations);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            AppLogger.Warn("Remote people metadata is unavailable; continuing media sync: " + ex.Message);
            metadataComplete = false;
        }
        foreach (var source in incremental.Supported ? [] : sources)
        {
            var localSource = await UiWork.RunDatabaseAsync("RemoteMirror.AddSource",
                () =>
                {
                    var local = Db.AddSource(source.Path, reconcileAliases: true);
                    local.ImageCount = Db.GetImageCount(local.Id);
                    return local;
                }, cancellationToken).ConfigureAwait(false);
            string? cursor = null;
            // Counts cannot detect renames, replacements or metadata edits.
            {
                do
                {
                    var page = await mirrorGateway.ListImagesAsync(source.Id, true, 500, cursor, cancellationToken).ConfigureAwait(false);
                    foreach (var batch in page.Items.Chunk(50))
                    {
                        foreach (var image in batch) image.SourceId = localSource.Id;
                        await UiWork.RunDatabaseAsync("RemoteMirror.Images.UpsertBatch", () =>
                        {
                            foreach (var image in batch) ValidateLocalMirrorIdentity("image", image.Id, image.Path);
                            Db.UpsertMirroredImageBatch(batch);
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    cursor = page.NextCursor;
                } while (cursor is not null);
            }

            cursor = null;
            try
            {
                do
                {
                    var page = await mirrorGateway.ListVideosAsync(source.Id, 500, cursor, cancellationToken).ConfigureAwait(false);
                    foreach (var batch in page.Items.Chunk(50))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await UiWork.RunDatabaseAsync("RemoteMirror.Videos.UpsertBatch", () =>
                        {
                            foreach (var video in batch) ValidateLocalMirrorIdentity("video", video.Id, video.Path);
                            Videos.UpsertMirroredBatch(batch, localSource.Id);
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    cursor = page.NextCursor;
                } while (cursor is not null);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                if (requireMetadataSync) throw;
                AppLogger.Warn("Remote video mirror deferred; image metadata sync will continue: " + ex.Message);
            }
        }
        try
        {
            // Apply relationships only after all mirrored media rows exist; the
            // foreign keys intentionally protect against partial remote syncs.
            var httpMirror = mirrorGateway as HttpIndexGateway;
            var metadata = httpMirror is null
                ? await mirrorGateway.GetMetadataAsync(cancellationToken).ConfigureAwait(false)
                : await httpMirror.GetMetadataAsync(cancellationToken, includeEmbeddings: false, includeFaceModels: true).ConfigureAwait(false);
            // The normal mirror payload omits vectors for speed. Fetch them
            // once when this mirror has not yet populated a model space; later
            // 30-second refreshes keep the existing vectors via COALESCE.
            if (httpMirror is not null && metadata.Faces.Count > 0)
            {
                var missingModelVectors = await UiWork.RunDatabaseAsync("RemoteMirror.VectorCoverage", () => metadata.Faces.Select(face => face.ModelVersion)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Any(model => VisionRepository.GetFaceEmbeddingCount(model) == 0), cancellationToken).ConfigureAwait(false);
                if (missingModelVectors)
                {
                    AppLogger.Info("Remote mirror is missing face vectors; requesting one full vector payload");
                    metadata = await httpMirror.GetMetadataAsync(cancellationToken, includeEmbeddings: true, includeFaceModels: true).ConfigureAwait(false);
                }
            }
            AppLogger.Info($"Remote metadata projection: faces={metadata.Faces.Count}, reviews={metadata.FaceReviewStatesOrEmpty.Count}, candidateClusters={metadata.FaceCandidateClustersOrEmpty.Count}, candidateMembers={metadata.FaceCandidateMembersOrEmpty.Count}, scenes={metadata.SceneGroupDescriptionsOrEmpty.Count}, videoKeyframes={metadata.VideoKeyframes.Count}, videoFaces={metadata.VideoFacesOrEmpty.Count}, transcripts={metadata.VideoTranscripts.Count}, summaries={metadata.VideoSummaries.Count}");
            // Face rows are part of the mirrored index, not local-only state. They
            // must be applied before assignments so the same people/faces are
            // visible in the remote client and can be searched there.
            if (incremental.Supported)
                await UiWork.RunDatabaseAsync("RemoteMirror.ReconcileRelationships", () =>
                    Db.ReconcileMirroredPhotoMetadata(metadata, authoritativeCatalog: ServiceMode && incremental.Supported), cancellationToken).ConfigureAwait(false);
            await UiWork.RunDatabaseAsync("RemoteMirror.Faces", () =>
                Db.UpsertMirroredFaces(metadata.Faces, metadata.FaceAssignments), cancellationToken).ConfigureAwait(false);
            await UiWork.RunDatabaseAsync("RemoteMirror.FaceWorkflow", () =>
                Db.ReplaceMirroredFaceWorkflow(metadata), cancellationToken).ConfigureAwait(false);
            if (metadata.FaceTrainingArtifacts is not null && metadata.FacePersonPrototypes is not null &&
                metadata.FacePersonPolicies is not null && metadata.FaceModelSettings is not null)
                await UiWork.RunDatabaseAsync("RemoteMirror.FaceTrainingMetadata", () =>
                    Db.UpsertMirroredFaceTrainingMetadata(metadata), cancellationToken).ConfigureAwait(false);
            await UiWork.RunDatabaseAsync("RemoteMirror.VideoMetadata", () =>
                Db.UpsertMirroredVideoMetadata(metadata, authoritative: ServiceMode && incremental.Supported), cancellationToken).ConfigureAwait(false);
            AppLogger.Info($"Remote metadata applied: scenes={metadata.SceneGroupDescriptionsOrEmpty.Count}; analyses={metadata.CloudVisionAnalysesOrEmpty.Count}; faces={metadata.Faces.Count}; videoKeyframes={metadata.VideoKeyframes.Count}; videoFaces={metadata.VideoFacesOrEmpty.Count}");
            foreach (var batch in metadata.AlbumImages.Chunk(50))
                await UiWork.RunDatabaseAsync("RemoteMirror.AlbumLinks", () =>
                {
                    foreach (var link in batch) SemanticEngine.AddToAlbum(Db, link.AlbumId, link.ImageId, recordEdit: false);
                }, cancellationToken).ConfigureAwait(false);
            foreach (var batch in metadata.ImageTags.Chunk(50))
                await UiWork.RunDatabaseAsync("RemoteMirror.TagLinks", () =>
                {
                    foreach (var link in batch) Db.UpsertMirroredImageTag(link.ImageId, link.TagId);
                }, cancellationToken).ConfigureAwait(false);
            await UiWork.RunDatabaseAsync("RemoteMirror.Locations", () =>
                Db.UpsertMirroredLocations(metadata.Locations), cancellationToken).ConfigureAwait(false);
            await UiWork.RunDatabaseAsync("RemoteMirror.ManualEdits", Db.ReplayMetadataEdits, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            if (requireMetadataSync) throw;
            AppLogger.Warn("Remote metadata links are unavailable; continuing media sync: " + ex.Message);
            metadataComplete = false;
        }
        if (metadataComplete && incremental.Checkpoint is { } completedCheckpoint)
            await UiWork.RunDatabaseAsync("RemoteMirror.MetadataCheckpoint", () =>
                Db.SaveMirrorCheckpoint(completedCheckpoint with { MetadataRevision = incremental.MetadataRevision }), cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _remoteMediaBootstrapped, true);
    }

    /// <summary>
    /// Refreshes only the remote video catalog. Video scan must not trigger the
    /// expensive full metadata/vector mirror; that operation can take minutes
    /// for a large library and previously made the Scan Videos button appear
    /// hung.
    /// </summary>
    public static async Task SyncRemoteVideosAsync(CancellationToken cancellationToken = default)
    {
        var mirrorGateway = ServiceMode ? Gateway : SyncGateway;
        if (mirrorGateway is null) return;
        var sources = await mirrorGateway.ListSourcesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var source in sources)
        {
            var localSource = await UiWork.RunDatabaseAsync("RemoteVideos.AddSource",
                () => Db.AddSource(source.Path, reconcileAliases: true), cancellationToken).ConfigureAwait(false);
            string? cursor = null;
            do
            {
                var page = await mirrorGateway.ListVideosAsync(source.Id, 500, cursor, cancellationToken).ConfigureAwait(false);
                foreach (var batch in page.Items.Chunk(50))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await UiWork.RunDatabaseAsync("RemoteVideos.UpsertBatch", () =>
                    {
                        foreach (var video in batch) ValidateLocalMirrorIdentity("video", video.Id, video.Path);
                        Videos.UpsertMirroredBatch(batch, localSource.Id);
                    }, cancellationToken).ConfigureAwait(false);
                }
                AppLogger.Info($"远程视频镜像写入进度：source={source.Id}; batch={page.Items.Count}; cursor={page.NextCursor ?? "end"}");
                cursor = page.NextCursor;
            } while (cursor is not null);
        }
    }

    public static async Task<IndexSyncClient.IndexSyncResult?> SyncLocalChangesToRemoteAsync(
        CancellationToken cancellationToken = default, IProgress<string>? progress = null)
    {
        await RemoteMirrorGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await SyncLocalChangesToRemoteCoreAsync(cancellationToken, progress).ConfigureAwait(false); }
        finally { RemoteMirrorGate.Release(); }
    }

    private static async Task<IndexSyncClient.IndexSyncResult?> SyncLocalChangesToRemoteCoreAsync(
        CancellationToken cancellationToken = default, IProgress<string>? progress = null)
    {
        if (SyncClient is null) return null;
        if (ServiceMode && !Volatile.Read(ref _remoteMediaBootstrapped))
            await SyncRemoteMirrorCoreAsync(cancellationToken, requireMetadataSync: true).ConfigureAwait(false);
        var checkpoint = Path.Combine(AppPaths.DataRoot,
            ServiceMode ? "remote-sync-checkpoint.json" : "index-sync-checkpoint.json");
        var result = await SyncClient.PushIncrementalAsync(Db, checkpoint, progress, cancellationToken,
            publishSceneLayouts: !ServiceMode, publishMedia: !ServiceMode).ConfigureAwait(false);
        await SyncClient.PushDirectoryClassificationAsync(
            new DirectoryClassificationPreferences(AppPaths.DirectoryClassificationSettingsPath).Snapshot(), cancellationToken).ConfigureAwait(false);
        AppLogger.Info($"增量索引发布完成：changed={result.Changed}; images={result.Images}; videos={result.Videos}");
        return result;
    }

    /// <summary>Publishes a committed index/calibration change without blocking the UI.
    /// In local mode this is active only when the user paired a remote IndexService.</summary>
    public static void QueueImmediateRemoteSync(string reason)
    {
        if (SyncClient is null) return;
        // A vector/media upload may already be in flight for minutes. Manual
        // review decisions have their own serialized small request path.
        _ = Task.Run(async () =>
        {
            try
            {
                await SyncClient.PushFaceReviewDecisionsAsync(Db).ConfigureAwait(false);
                AppLogger.Info("人工人脸状态同步完成");
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                AppLogger.Warn("人工人脸状态同步未完成，操作日志已保留等待重试");
            }
        });
        Interlocked.Exchange(ref _remoteSyncRequested, 1);
        if (Interlocked.CompareExchange(ref _remoteSyncWorkerRunning, 1, 0) != 0) return;
        _ = Task.Run(() => RunImmediateRemoteSyncAsync(reason));
    }

    private static async Task RunImmediateRemoteSyncAsync(string reason)
    {
        try
        {
            do
            {
                Interlocked.Exchange(ref _remoteSyncRequested, 0);
                try
                {
                    AppLogger.Info($"远程索引即时同步开始：reason={reason}");
                    await SyncLocalChangesToRemoteAsync().ConfigureAwait(false);
                    AppLogger.Info($"远程索引即时同步完成：reason={reason}");
                }
                catch (Exception exception)
                {
                    AppLogger.Warn($"远程索引即时同步失败，将由后台重试：reason={reason}; {exception.Message}");
                }
            } while (Volatile.Read(ref _remoteSyncRequested) != 0);
        }
        finally
        {
            Volatile.Write(ref _remoteSyncWorkerRunning, 0);
            // A write may arrive between the loop's final check and clearing
            // the worker flag. Re-arm the worker without losing that request.
            if (Volatile.Read(ref _remoteSyncRequested) != 0 &&
                Interlocked.CompareExchange(ref _remoteSyncWorkerRunning, 1, 0) == 0)
                _ = Task.Run(() => RunImmediateRemoteSyncAsync(reason));
        }
    }

    public static (CloudVisionAnalysisService Service, OpenAiCompatibleCloudVisionAnalyzer Analyzer, CloudVisionRunPolicy Policy, string ProviderLabel, bool IsLocal)? CreateCloudVisionService(string featureId = AiFeatureIds.PhotoAnalysis)
    {
        var settings = CloudVisionSettings.Load(AppPaths.CloudVisionSettingsPath);
        if (!settings.Enabled) return null;
        var resolved = settings.Resolve(featureId);
        if (resolved is null) return null;
        var provider = resolved.Value.Provider;
        var model = resolved.Value.Model;
        var isLocal = AiProviderIds.IsLocal(provider.Id);
        TryGetAiServiceBackend(out var serviceBackend);
        if (serviceBackend.BaseUrl is { Length: > 0 } configuredService
            && Uri.TryCreate(configuredService.TrimEnd('/') + "/api/ai/v1/chat/stream", UriKind.Absolute, out var configuredEndpoint))
        {
            var routedClient = CreatePinnedServiceClient(serviceBackend, TimeSpan.FromMinutes(5));
            var routed = new OpenAiCompatibleCloudVisionAnalyzer(new CloudVisionApiOptions(configuredEndpoint, serviceBackend.Token ?? "", model.Id, "index-service", PromptVersion: "picer-photo-v2-concise"), routedClient, disposeClient: true);
            return (new CloudVisionAnalysisService(CloudVisionRepository, routed, Geocoder, People,
                databaseWork: (work, ct) => UiWork.RunDatabaseAsync(work, ct)), routed,
                new CloudVisionRunPolicy(true, settings.MaximumImagesPerRun, settings.IncludePreciseGps,
                    isLocal ? decimal.MaxValue : settings.DailyBudgetYuan, isLocal ? decimal.MaxValue : settings.MonthlyBudgetYuan,
                    EstimatedRequestCostYuan: isLocal ? 0m : 0.02m),
                $"IndexService / {model.Name}", isLocal);
        }
        AppLogger.Warn($"AI视觉请求未通过 IndexService，已拒绝客户端直连；feature={featureId}; provider={provider.Id}");
        return null;
    }

    public static ISceneGroupSummarizer? CreateSceneGroupSummarizer()
    {
        if (TryGetAiServiceBackend(out var configuredBackend)
            && configuredBackend.BaseUrl is { Length: > 0 } configuredService
            && Uri.TryCreate(configuredService.TrimEnd('/') + "/", UriKind.Absolute, out var configuredServiceEndpoint))
            return new ServiceSceneGroupSummarizer(configuredServiceEndpoint, configuredBackend.Token ?? "", client: CreatePinnedServiceClient(configuredBackend, TimeSpan.FromMinutes(5)));
        if (ServiceMode)
        {
            var backend = AppPaths.LoadIndexBackend();
            if (backend.BaseUrl is { Length: > 0 } remoteUrl && Uri.TryCreate(remoteUrl.TrimEnd('/') + "/", UriKind.Absolute, out var serviceEndpoint))
            {
                AppLogger.Info("远程场景摘要使用 IndexService VL 路由");
                return new ServiceSceneGroupSummarizer(serviceEndpoint, backend.Token ?? "", client: CreatePinnedServiceClient(backend, TimeSpan.FromMinutes(5)));
            }
            AppLogger.Warn("远程场景摘要不可用：IndexService 端点未配置");
            return null;
        }
        if (!TryGetAiServiceBackend(out _))
        {
            AppLogger.Warn("场景摘要未配置 IndexService，已拒绝客户端直连");
            return null;
        }
        AppLogger.Warn("场景摘要 IndexService 端点不可用");
        return null;
    }

    public static IVideoTranscriptionService? CreateVideoTranscriptionService()
    {
        if (TryGetAiServiceBackend(out var configuredBackend)
            && configuredBackend.BaseUrl is { Length: > 0 } configuredService
            && Uri.TryCreate(configuredService.TrimEnd('/') + "/", UriKind.Absolute, out var configuredServiceEndpoint))
        {
            AppLogger.Info($"视频转写统一使用 IndexService 中继；mode={(ServiceMode ? "remote" : "local")}");
            return new ServiceVideoTranscriptionService(configuredServiceEndpoint, configuredBackend.Token ?? "");
        }
        if (ServiceMode)
        {
            var backend = AppPaths.LoadIndexBackend();
            if (backend.BaseUrl is { Length: > 0 } baseUrl && Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var serviceEndpoint))
            {
                AppLogger.Info("远程视频转写使用 IndexService Service 路由");
                return new ServiceVideoTranscriptionService(serviceEndpoint, backend.Token ?? "");
            }
            AppLogger.Warn("远程视频转写不可用：IndexService 端点未配置");
            return null;
        }
        AppLogger.Warn("视频转写未配置 IndexService，已拒绝客户端直连");
        return null;
    }

    public static OpenAiCompatibleChatClient? CreateAiChatClient(string featureId)
    {
        var settings = CloudVisionSettings.Load(AppPaths.CloudVisionSettingsPath);
        if (!settings.Enabled) { AppLogger.Warn($"AI chat client unavailable: settings disabled; feature={featureId}"); return null; }
        var resolved = settings.Resolve(featureId);
        if (resolved is null) { AppLogger.Warn($"AI chat client unavailable: feature binding missing; feature={featureId}"); return null; }
        var provider = resolved.Value.Provider; var model = resolved.Value.Model;
        var isLocal = AiProviderIds.IsLocal(provider.Id);
        if (TryGetAiServiceBackend(out var backend))
        {
            if (backend.BaseUrl is { Length: > 0 } remoteBase && !string.IsNullOrWhiteSpace(backend.Token)
                && Uri.TryCreate(remoteBase.TrimEnd('/') + "/api/ai/v1/chat/stream", UriKind.Absolute, out var remoteEndpoint))
            {
                AppLogger.Info($"AI chat client routed through IndexService; feature={featureId}");
                var handler = new HttpClientHandler();
                handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is null) return false;
                    if (backend.SpkiPin is null) return true;
                    var pin = HttpIndexGateway.ComputeSpkiPin(new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate));
                    return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(pin, backend.SpkiPin);
                };
                var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
                return new OpenAiCompatibleChatClient(remoteEndpoint, backend.Token, model.Id, client, disposeClient: true);
            }
            AppLogger.Warn($"AI chat client unavailable: remote IndexService endpoint missing; feature={featureId}");
            return null;
        }
        AppLogger.Warn($"AI chat client unavailable: IndexService endpoint missing; feature={featureId}");
        return null;
    }

    public static void Shutdown()
    {
        AppLogger.Info("AppServices 关闭");
        if (ServiceMode)
        {
            (Gateway as IDisposable)?.Dispose();
            return;
        }
        VisionAnalysis?.Dispose();
        FaceTraining?.Dispose();
        (VideoKeyframes as IDisposable)?.Dispose();
        (VideoTranscription as IDisposable)?.Dispose();
        (FaceAnalyzer as IDisposable)?.Dispose();
        ClipEncoder?.Dispose();
        Db?.Dispose();
        (Geocoder as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Resolves the IndexService endpoint for both GUI modes.  Local mode
    /// keeps its own SQLite/index, but AI requests still use the paired
    /// service endpoint; there is intentionally no localhost model fallback.
    /// </summary>
    private static bool TryGetAiServiceBackend(out IndexBackendOptions backend)
    {
        try
        {
            var configured = AppPaths.LoadIndexBackend();
            if (configured.BaseUrl is { Length: > 0 })
            {
                backend = configured;
                return true;
            }

            var paired = IndexBackendConfig.LoadConfiguredServiceEndpoint(
                AppPaths.ServiceEndpointPath,
                static cipher => System.Security.Cryptography.ProtectedData.Unprotect(
                    cipher, null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
            if (paired?.BaseUrl is { Length: > 0 })
            {
                backend = paired;
                return true;
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warn("IndexService endpoint unavailable for AI relay: " + exception.GetType().Name);
        }

        backend = new IndexBackendOptions(IndexBackendConfig.LocalMode, null, null, null);
        return false;
    }

    /// <summary>
    /// Creates the HTTP client used for AI calls through IndexService. The
    /// service is intentionally certificate-pinned rather than relying on the
    /// machine trust store, because the paired endpoint uses a private cert.
    /// </summary>
    public static string? TrainingServiceEndpoint => TryGetAiServiceBackend(out var backend) ? backend.BaseUrl : null;

    public static FaceTrainingSubmissionService CreateTrainingSubmissionService() => new(CreateTrainingHttpClient());

    public static HttpClient CreateTrainingHttpClient()
    {
        if (!TryGetAiServiceBackend(out var backend)) throw new InvalidOperationException("训练服务尚未配对");
        var client = CreatePinnedServiceClient(backend, TimeSpan.FromMinutes(10));
        client.BaseAddress = new Uri(backend.BaseUrl!.TrimEnd('/') + "/api/service/v1/training/");
        return client;
    }

    internal static HttpClient CreatePinnedServiceClient(IndexBackendOptions backend, TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                if (backend.SpkiPin is null || backend.SpkiPin.Length == 0) return false;
                var actual = HttpIndexGateway.ComputeSpkiPin(new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate));
                return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actual, backend.SpkiPin);
            }
        };
        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", backend.Token ?? "");
        return client;
    }
}
