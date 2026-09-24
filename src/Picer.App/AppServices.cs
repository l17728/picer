using System.IO;
using System.Net.Http;
using Microsoft.ML.OnnxRuntime;
using Picer.App.FileOperations;
using Picer.App.Security;
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

namespace Picer.App;

public static class AppServices
{
    private static bool _remoteMediaBootstrapped;
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
    public static FfmpegKeyframeService? VideoKeyframes { get; private set; }
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
            FaceTraining = new FaceTrainingService(VisionRepository);
            CloudVisionRepository = new CloudVisionRepository(Db);
            VisualConceptRepository = new VisualConceptRepository(Db);
            Videos = new VideoRepository(Db);
            MediaSearch = new MediaSearchTool(Db, Videos);
            VideoIndex = new VideoIndexService(Db, Videos);
            VideoSummaries = new VideoSummaryService(Videos, Path.Combine(AppPaths.DataRoot, "remote-video-summary-cache"));
            ClipRepository = new ClipSemanticRepository(Db);
            FlorenceResults = new FlorenceResultRepository(Db);
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
        FaceTraining = new FaceTrainingService(VisionRepository);
        CloudVisionRepository = new CloudVisionRepository(Db);
        VisualConceptRepository = new VisualConceptRepository(Db);
        Videos = new VideoRepository(Db);
        MediaSearch = new MediaSearchTool(Db, Videos);
        VideoIndex = new VideoIndexService(Db, Videos);
        VideoSummaries = new VideoSummaryService(Videos, Path.Combine(AppPaths.DataRoot, "video-summary-cache"));
        ClipRepository = new ClipSemanticRepository(Db);
        FlorenceResults = new FlorenceResultRepository(Db);
        var ffmpeg = new FfmpegLocator().Find();
        VideoKeyframes = ffmpeg is null ? null : new FfmpegKeyframeService(ffmpeg, Path.Combine(AppPaths.DataRoot, "video-keyframes"));
        var whisper = AppPaths.BundledWhisperPath;
        var whisperModel = AppPaths.BundledWhisperModelPath;
        VideoTranscription = ffmpeg is not null && File.Exists(whisper) && File.Exists(whisperModel)
            ? new WhisperTranscriptionService(ffmpeg, whisper, whisperModel, Path.Combine(AppPaths.DataRoot, "video-audio")) : null;
        try
        {
            var fast = new YuNetSFaceAnalyzer(AppPaths.YuNetModelPath, AppPaths.SFaceModelPath, 320);
            var fallback = new YuNetSFaceAnalyzer(AppPaths.YuNetModelPath, AppPaths.SFaceModelPath, 640);
            FaceAnalyzer = new AdaptiveFaceAnalyzer(fast, fallback);
            VisionAnalysis = new VisionAnalysisService(VisionRepository, FaceAnalyzer);
            VideoFaceTimeline = new VideoFaceTimelineService(Db, FaceAnalyzer, VisionRepository, People);
            VisionUnavailableReason = null;
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
            ClipEncoder = new ChineseClipEncoder(AppPaths.ChineseClipModelPath, AppPaths.ChineseClipVocabularyPath);
            ClipSemantic = new ClipSemanticService(Db, ClipRepository, ClipEncoder);
            ClipUnavailableReason = null;
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

    public static async Task SyncRemoteMirrorAsync(CancellationToken cancellationToken = default)
    {
        var mirrorGateway = ServiceMode ? Gateway : SyncGateway;
        if (mirrorGateway is null) return;
        var fullMediaReconciliation = !Volatile.Read(ref _remoteMediaBootstrapped);
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
        // Metadata routes were added after the initial index-service deployment. Keep
        // image/video mirroring compatible with an older service while it is being
        // upgraded; each collection is optional and can be retried on the next sync.
        try
        {
            foreach (var tag in await mirrorGateway.ListTagsAsync(cancellationToken).ConfigureAwait(false))
                Db.UpsertMirroredTag(tag);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            AppLogger.Warn("Remote tag metadata is unavailable; continuing media sync: " + ex.Message);
        }
        try
        {
            foreach (var album in await mirrorGateway.ListAlbumsAsync(cancellationToken).ConfigureAwait(false))
                Db.UpsertMirroredAlbum(album);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            AppLogger.Warn("Remote album metadata is unavailable; continuing media sync: " + ex.Message);
        }
        try
        {
            foreach (var person in await mirrorGateway.ListPeopleAsync(cancellationToken).ConfigureAwait(false))
                Db.UpsertMirroredPerson(person);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            AppLogger.Warn("Remote people metadata is unavailable; continuing media sync: " + ex.Message);
        }
        foreach (var source in sources)
        {
            var existingSources = Db.GetSources();
            var localSource = existingSources.FirstOrDefault(item => string.Equals(item.Path, source.Path, StringComparison.OrdinalIgnoreCase))
                ?? (existingSources.Count == 1 && existingSources[0].ImageCount == source.ImageCount
                    ? existingSources[0]
                    : Db.AddSource(source.Path));
            var mediaComplete = !fullMediaReconciliation && localSource.ImageCount == source.ImageCount && source.ImageCount > 0;
            string? cursor = null;
            if (!mediaComplete)
            {
                do
                {
                    var page = await mirrorGateway.ListImagesAsync(source.Id, false, 500, cursor, cancellationToken).ConfigureAwait(false);
                    foreach (var image in page.Items)
                    {
                        image.SourceId = localSource.Id;
                        Db.UpsertMirroredImage(image);
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
                    foreach (var video in page.Items)
                        Videos.UpsertMirrored(video, localSource.Id);
                    cursor = page.NextCursor;
                } while (cursor is not null);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                AppLogger.Warn("Remote video mirror deferred; image metadata sync will continue: " + ex.Message);
            }
        }
        Volatile.Write(ref _remoteMediaBootstrapped, true);
        try
        {
            // Apply relationships only after all mirrored media rows exist; the
            // foreign keys intentionally protect against partial remote syncs.
            var metadata = await mirrorGateway.GetMetadataAsync(cancellationToken).ConfigureAwait(false);
            // The normal mirror payload omits vectors for speed. Fetch them
            // once when this mirror has not yet populated a model space; later
            // 30-second refreshes keep the existing vectors via COALESCE.
            if (mirrorGateway is HttpIndexGateway httpGateway && metadata.Faces.Count > 0)
            {
                var missingModelVectors = metadata.Faces.Select(face => face.ModelVersion)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Any(model => VisionRepository.GetFaceEmbeddingCount(model) == 0);
                if (missingModelVectors)
                {
                    AppLogger.Info("Remote mirror is missing face vectors; requesting one full vector payload");
                    metadata = await httpGateway.GetMetadataAsync(cancellationToken, includeEmbeddings: true).ConfigureAwait(false);
                }
            }
            AppLogger.Info($"Remote metadata projection: faces={metadata.Faces.Count}, reviews={metadata.FaceReviewStatesOrEmpty.Count}, candidateClusters={metadata.FaceCandidateClustersOrEmpty.Count}, candidateMembers={metadata.FaceCandidateMembersOrEmpty.Count}, scenes={metadata.SceneGroupDescriptionsOrEmpty.Count}");
            // Face rows are part of the mirrored index, not local-only state. They
            // must be applied before assignments so the same people/faces are
            // visible in the remote client and can be searched there.
            Db.UpsertMirroredFaces(metadata.Faces, metadata.FaceAssignments);
            Db.ReplaceMirroredFaceWorkflow(metadata);
            Db.UpsertMirroredVideoMetadata(metadata);
            AppLogger.Info($"Remote metadata applied: scenes={Db.GetAllMirroredSceneGroupDescriptions().Count}; analyses={Db.GetAllMirroredCloudVisionAnalyses().Count}; faces={Db.GetAllMirroredFaces().Count}");
            foreach (var link in metadata.AlbumImages)
                SemanticEngine.AddToAlbum(Db, link.AlbumId, link.ImageId);
            foreach (var link in metadata.ImageTags)
                Db.UpsertMirroredImageTag(link.ImageId, link.TagId);
            Db.UpsertMirroredLocations(metadata.Locations);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            AppLogger.Warn("Remote metadata links are unavailable; continuing media sync: " + ex.Message);
        }
    }

    public static async Task<IndexSyncClient.IndexSyncResult?> SyncLocalChangesToRemoteAsync(
        CancellationToken cancellationToken = default, IProgress<string>? progress = null)
    {
        if (SyncClient is null) return null;
        var checkpoint = Path.Combine(AppPaths.DataRoot,
            ServiceMode ? "remote-sync-checkpoint.json" : "index-sync-checkpoint.json");
        var result = await SyncClient.PushIncrementalAsync(Db, checkpoint, progress, cancellationToken,
            publishSceneLayouts: !ServiceMode).ConfigureAwait(false);
        await SyncClient.PushDirectoryClassificationAsync(
            new DirectoryClassificationPreferences(AppPaths.DirectoryClassificationSettingsPath).Snapshot(), cancellationToken).ConfigureAwait(false);
        AppLogger.Info($"增量索引发布完成：changed={result.Changed}; images={result.Images}; videos={result.Videos}");
        return result;
    }

    public static (CloudVisionAnalysisService Service, OpenAiCompatibleCloudVisionAnalyzer Analyzer, CloudVisionRunPolicy Policy, string ProviderLabel, bool IsLocal)? CreateCloudVisionService(string featureId = AiFeatureIds.PhotoAnalysis)
    {
        var settings = CloudVisionSettings.Load(AppPaths.CloudVisionSettingsPath);
        if (!settings.Enabled) return null;
        var resolved = settings.Resolve(featureId);
        if (resolved is null) return null;
        var provider = resolved.Value.Provider;
        var model = resolved.Value.Model;
        var credentials = new WindowsCredentialStore();
        var isLocal = AiProviderIds.IsLocal(provider.Id);
        var key = isLocal ? "local-proxy" : credentials.Read(PicerCredentialTargets.AiProvider(provider.Id));
        if (string.IsNullOrWhiteSpace(key) && provider.Id.Equals("bailian", StringComparison.OrdinalIgnoreCase)) key = credentials.Read(PicerCredentialTargets.Bailian);
        if (string.IsNullOrWhiteSpace(key) && provider.Id.Equals("zhipu", StringComparison.OrdinalIgnoreCase)) key = credentials.Read(PicerCredentialTargets.Zhipu);
        if (string.IsNullOrWhiteSpace(key)) return null;
        var baseUrl = provider.BaseUrl.TrimEnd('/');
        var endpointText = isLocal || baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? baseUrl : baseUrl + "/chat/completions";
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint)) return null;
        var options = new CloudVisionApiOptions(endpoint, key, model.Id, provider.Id, PromptVersion: "picer-photo-v2-concise");
        var analyzer = new OpenAiCompatibleCloudVisionAnalyzer(options);
        return (new CloudVisionAnalysisService(CloudVisionRepository, analyzer, Geocoder, People), analyzer,
            new CloudVisionRunPolicy(true, settings.MaximumImagesPerRun, settings.IncludePreciseGps,
                isLocal ? decimal.MaxValue : settings.DailyBudgetYuan,
                isLocal ? decimal.MaxValue : settings.MonthlyBudgetYuan),
            $"{provider.Name} / {model.Name}", isLocal);
    }

    public static OpenAiCompatibleSceneSummarizer? CreateSceneGroupSummarizer()
    {
        var settings = CloudVisionSettings.Load(AppPaths.CloudVisionSettingsPath);
        if (!settings.Enabled) return null;
        var resolved = settings.Resolve(AiFeatureIds.SceneDescription);
        if (resolved is null) return null;
        var provider = resolved.Value.Provider;
        var model = resolved.Value.Model;
        var credentials = new WindowsCredentialStore();
        var isLocal = AiProviderIds.IsLocal(provider.Id);
        var key = isLocal ? "local-proxy" : credentials.Read(PicerCredentialTargets.AiProvider(provider.Id));
        if (string.IsNullOrWhiteSpace(key) && provider.Id.Equals("bailian", StringComparison.OrdinalIgnoreCase)) key = credentials.Read(PicerCredentialTargets.Bailian);
        if (string.IsNullOrWhiteSpace(key) && provider.Id.Equals("zhipu", StringComparison.OrdinalIgnoreCase)) key = credentials.Read(PicerCredentialTargets.Zhipu);
        if (string.IsNullOrWhiteSpace(key)) return null;
        var baseUrl = provider.BaseUrl.TrimEnd('/');
        var endpointText = isLocal || baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? baseUrl : baseUrl + "/chat/completions";
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint)) return null;
        return new OpenAiCompatibleSceneSummarizer(new CloudVisionApiOptions(endpoint, key, model.Id, provider.Id));
    }

    public static IVideoTranscriptionService? CreateVideoTranscriptionService()
    {
        var ffmpeg = new FfmpegLocator().Find();
        if (ffmpeg is null) return null;
        var settings = CloudVisionSettings.Load(AppPaths.CloudVisionSettingsPath);
        var resolved = settings.Resolve(AiFeatureIds.VideoTranscription);
        if (resolved is not null && AiProviderIds.IsLocal(resolved.Value.Provider.Id))
        {
            if (resolved.Value.Provider.Id.Equals(AiProviderIds.LocalQwenAsr, StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(resolved.Value.Provider.BaseUrl, UriKind.Absolute, out var endpoint))
                return new QwenAsrTranscriptionService(ffmpeg, endpoint, resolved.Value.Model.Id,
                    Path.Combine(AppPaths.DataRoot, "video-audio"));
            if (resolved.Value.Provider.Id.Equals(AiProviderIds.BundledWhisper, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(AppPaths.BundledWhisperPath) && File.Exists(AppPaths.BundledWhisperModelPath))
                return new WhisperTranscriptionService(ffmpeg, AppPaths.BundledWhisperPath,
                    AppPaths.BundledWhisperModelPath, Path.Combine(AppPaths.DataRoot, "video-audio"));
        }
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
        if (ServiceMode)
        {
            var backend = AppPaths.LoadIndexBackend();
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
        var key = isLocal ? "local-proxy" : new WindowsCredentialStore().Read(PicerCredentialTargets.AiProvider(provider.Id));
        if (string.IsNullOrWhiteSpace(key)) { AppLogger.Warn($"AI chat client unavailable: credential missing; provider={provider.Id}"); return null; }
        var baseUrl = provider.BaseUrl.TrimEnd('/');
        var target = isLocal || baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? baseUrl : baseUrl + "/chat/completions";
        if (!Uri.TryCreate(target, UriKind.Absolute, out var endpoint))
        { AppLogger.Warn($"AI chat client unavailable: endpoint invalid; provider={provider.Id}"); return null; }
        AppLogger.Info($"AI chat client created; feature={featureId}; provider={provider.Id}; local={isLocal}");
        return new OpenAiCompatibleChatClient(endpoint, key, model.Id);
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
        (FaceAnalyzer as IDisposable)?.Dispose();
        ClipEncoder?.Dispose();
        Db?.Dispose();
        (Geocoder as IDisposable)?.Dispose();
    }
}
