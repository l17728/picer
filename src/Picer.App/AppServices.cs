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
    public static IIndexGateway Gateway { get; private set; } = null!;
    public static bool ServiceMode { get; private set; }
    public static PicerDatabase Db { get; private set; } = null!;
    public static IStorageProvider Storage { get; private set; } = null!;
    public static ThumbnailService Thumbnails { get; private set; } = null!;
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
        if (!ServiceMode) return;
        var sources = await Gateway.ListSourcesAsync(cancellationToken).ConfigureAwait(false);
        // Metadata routes were added after the initial index-service deployment. Keep
        // image/video mirroring compatible with an older service while it is being
        // upgraded; each collection is optional and can be retried on the next sync.
        try
        {
            foreach (var tag in await Gateway.ListTagsAsync(cancellationToken).ConfigureAwait(false))
                Db.UpsertMirroredTag(tag);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            AppLogger.Warn("Remote tag metadata is unavailable; continuing media sync: " + ex.Message);
        }
        try
        {
            foreach (var album in await Gateway.ListAlbumsAsync(cancellationToken).ConfigureAwait(false))
                Db.UpsertMirroredAlbum(album);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            AppLogger.Warn("Remote album metadata is unavailable; continuing media sync: " + ex.Message);
        }
        try
        {
            foreach (var person in await Gateway.ListPeopleAsync(cancellationToken).ConfigureAwait(false))
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
            var mediaComplete = localSource.ImageCount == source.ImageCount && source.ImageCount > 0;
            string? cursor = null;
            if (!mediaComplete)
            {
                do
                {
                    var page = await Gateway.ListImagesAsync(source.Id, false, 500, cursor, cancellationToken).ConfigureAwait(false);
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
                    var page = await Gateway.ListVideosAsync(source.Id, 500, cursor, cancellationToken).ConfigureAwait(false);
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
        try
        {
            // Apply relationships only after all mirrored media rows exist; the
            // foreign keys intentionally protect against partial remote syncs.
            var metadata = await Gateway.GetMetadataAsync(cancellationToken).ConfigureAwait(false);
            // Face rows are part of the mirrored index, not local-only state. They
            // must be applied before assignments so the same people/faces are
            // visible in the remote client and can be searched there.
            Db.UpsertMirroredFaces(metadata.Faces, metadata.FaceAssignments);
            Db.UpsertMirroredVideoMetadata(metadata);
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
        if (!settings.Enabled) return null;
        var resolved = settings.Resolve(featureId);
        if (resolved is null) return null;
        var provider = resolved.Value.Provider; var model = resolved.Value.Model;
        var isLocal = AiProviderIds.IsLocal(provider.Id);
        var key = isLocal ? "local-proxy" : new WindowsCredentialStore().Read(PicerCredentialTargets.AiProvider(provider.Id));
        if (string.IsNullOrWhiteSpace(key)) return null;
        var baseUrl = provider.BaseUrl.TrimEnd('/');
        var target = isLocal || baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? baseUrl : baseUrl + "/chat/completions";
        return Uri.TryCreate(target, UriKind.Absolute, out var endpoint)
            ? new OpenAiCompatibleChatClient(endpoint, key, model.Id) : null;
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
