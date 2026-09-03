using System.IO;
using Microsoft.ML.OnnxRuntime;
using Picer.App.FileOperations;
using Picer.App.Security;
using Picer.Core.Data;
using Picer.Core.FileOperations;
using Picer.Core.Indexing;
using Picer.Core.Geocoding;
using Picer.Core.Logging;
using Picer.Core.People;
using Picer.Core.Storage;
using Picer.Core.Thumbnails;
using Picer.Core.Vision;
using Picer.Core.Video;
using Picer.Core.SemanticSearch;

namespace Picer.App;

public static class AppServices
{
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
    public static CloudVisionRepository CloudVisionRepository { get; private set; } = null!;
    public static VisualConceptRepository VisualConceptRepository { get; private set; } = null!;
    public static VideoRepository Videos { get; private set; } = null!;
    public static VideoIndexService VideoIndex { get; private set; } = null!;
    public static FfmpegKeyframeService? VideoKeyframes { get; private set; }
    public static WhisperTranscriptionService? VideoTranscription { get; private set; }
    public static VideoFaceTimelineService? VideoFaceTimeline { get; private set; }
    public static VideoSummaryService VideoSummaries { get; private set; } = null!;
    public static ClipSemanticRepository ClipRepository { get; private set; } = null!;
    public static FlorenceResultRepository FlorenceResults { get; private set; } = null!;
    public static ClipSemanticService? ClipSemantic { get; private set; }
    public static string? ClipUnavailableReason { get; private set; }
    private static ChineseClipEncoder? ClipEncoder { get; set; }
    private static YuNetSFaceAnalyzer? FaceAnalyzer { get; set; }

    private static string ConfigPath => Path.Combine(AppPaths.DataRoot, "storage.json");

    public static void Initialize()
    {
        AppLogger.Initialize(Path.Combine(AppPaths.DataRoot, "logs"));
        AppLogger.Info("AppServices 初始化开始");

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
        CloudVisionRepository = new CloudVisionRepository(Db);
        VisualConceptRepository = new VisualConceptRepository(Db);
        Videos = new VideoRepository(Db);
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
            FaceAnalyzer = new YuNetSFaceAnalyzer(AppPaths.YuNetModelPath, AppPaths.SFaceModelPath);
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
    }

    public static void Reinitialize(IStorageProvider provider)
    {
        Storage = provider;
        Index = new IndexEngine(Db, Storage, Thumbnails);
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

    public static (CloudVisionAnalysisService Service, OpenAiCompatibleCloudVisionAnalyzer Analyzer, CloudVisionRunPolicy Policy, string ProviderLabel)? CreateCloudVisionService(string featureId = AiFeatureIds.PhotoAnalysis)
    {
        var settings = CloudVisionSettings.Load(AppPaths.CloudVisionSettingsPath);
        if (!settings.Enabled) return null;
        var resolved = settings.Resolve(featureId);
        if (resolved is null) return null;
        var provider = resolved.Value.Provider;
        var model = resolved.Value.Model;
        var credentials = new WindowsCredentialStore();
        var key = credentials.Read(PicerCredentialTargets.AiProvider(provider.Id));
        if (string.IsNullOrWhiteSpace(key) && provider.Id.Equals("bailian", StringComparison.OrdinalIgnoreCase)) key = credentials.Read(PicerCredentialTargets.Bailian);
        if (string.IsNullOrWhiteSpace(key) && provider.Id.Equals("zhipu", StringComparison.OrdinalIgnoreCase)) key = credentials.Read(PicerCredentialTargets.Zhipu);
        if (string.IsNullOrWhiteSpace(key)) return null;
        var baseUrl = provider.BaseUrl.TrimEnd('/');
        var endpointText = baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? baseUrl : baseUrl + "/chat/completions";
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint)) return null;
        var options = new CloudVisionApiOptions(endpoint, key, model.Id, provider.Id, PromptVersion: "picer-photo-v1");
        var analyzer = new OpenAiCompatibleCloudVisionAnalyzer(options);
        return (new CloudVisionAnalysisService(CloudVisionRepository, analyzer, Geocoder), analyzer,
            new CloudVisionRunPolicy(true, settings.MaximumImagesPerRun, settings.IncludePreciseGps,
                settings.DailyBudgetYuan, settings.MonthlyBudgetYuan),
            $"{provider.Name} / {model.Name}");
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
        var key = credentials.Read(PicerCredentialTargets.AiProvider(provider.Id));
        if (string.IsNullOrWhiteSpace(key) && provider.Id.Equals("bailian", StringComparison.OrdinalIgnoreCase)) key = credentials.Read(PicerCredentialTargets.Bailian);
        if (string.IsNullOrWhiteSpace(key) && provider.Id.Equals("zhipu", StringComparison.OrdinalIgnoreCase)) key = credentials.Read(PicerCredentialTargets.Zhipu);
        if (string.IsNullOrWhiteSpace(key)) return null;
        var baseUrl = provider.BaseUrl.TrimEnd('/');
        var endpointText = baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? baseUrl : baseUrl + "/chat/completions";
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint)) return null;
        return new OpenAiCompatibleSceneSummarizer(new CloudVisionApiOptions(endpoint, key, model.Id, provider.Id));
    }

    public static void Shutdown()
    {
        AppLogger.Info("AppServices 关闭");
        VisionAnalysis?.Dispose();
        FaceAnalyzer?.Dispose();
        ClipEncoder?.Dispose();
        Db?.Dispose();
        (Geocoder as IDisposable)?.Dispose();
    }
}
