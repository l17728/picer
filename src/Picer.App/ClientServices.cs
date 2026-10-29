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

/// <summary>Injected client composition: gateways, local mirror projections and authorized operations.</summary>
public sealed class ClientServices
{
    public IIndexGateway Gateway { get; set; } = default!;
    public IndexSyncClient? SyncClient { get; set; } = default!;
    public bool ServiceMode { get; set; } = default!;
    public PicerDatabase Db { get; set; } = default!;
    public IStorageProvider Storage { get; set; } = default!;
    public RemoteThumbnailCache RemoteThumbnails { get; set; } = default!;
    public IUserAuthorizedFileOperation FileOperations { get; set; } = default!;
    public LocationHierarchyService LocationHierarchy { get; set; } = default!;
    public StorageConfig Config { get; set; } = default!;
    public PeopleService People { get; set; } = default!;
    public CloudVisionRepository CloudVisionRepository { get; set; } = default!;
    public MediaSearchTool MediaSearch { get; set; } = default!;
    public VisualConceptRepository VisualConceptRepository { get; set; } = default!;
    public VideoRepository Videos { get; set; } = default!;
    internal string? RemoteVideoCacheRoot { get; set; }
    public VideoIndexService VideoIndex { get; set; } = default!;
    public IVideoMediaProcessor? VideoKeyframes { get; set; } = default!;
    public IVideoTranscriptionService? VideoTranscription { get; set; } = default!;
    public VideoFaceTimelineService? VideoFaceTimeline { get; set; } = default!;
    public VideoSummaryService VideoSummaries { get; set; } = default!;
    public string? TrainingServiceEndpoint { get; set; }
    public Func<string, OpenAiCompatibleChatClient?> ChatFactory { get; set; } = _ => null;
    public Func<string, (CloudVisionAnalysisService Service, OpenAiCompatibleCloudVisionAnalyzer Analyzer, CloudVisionRunPolicy Policy, string ProviderLabel, bool IsLocal)?> VisionFactory { get; set; } = _ => null;
    public Func<ISceneGroupSummarizer?> SceneFactory { get; set; } = () => null;
    public Func<IVideoTranscriptionService?> TranscriptionFactory { get; set; } = () => null;
    public Func<FaceTrainingSubmissionService> TrainingFactory { get; set; } = () => throw new InvalidOperationException("训练服务尚未配置");
    public Func<VideoRecord, CancellationToken, Task>? PurgeVideo { get; set; }
    public Func<int, bool>? ConfirmVideoPurge { get; set; }
    public Action<string> PublishChanges { get; set; } = _ => { };
    public Func<CancellationToken, bool, Task> RefreshMirror { get; set; } = (_, _) => Task.CompletedTask;
    public Func<CancellationToken, Task> RefreshVideos { get; set; } = _ => Task.CompletedTask;
    public Func<CancellationToken, IProgress<string>?, Task<IndexSyncClient.IndexSyncResult?>> PushChanges { get; set; } = (_, _) => Task.FromResult<IndexSyncClient.IndexSyncResult?>(null);
    public OpenAiCompatibleChatClient? CreateAiChatClient(string featureId) => ChatFactory(featureId);
    public (CloudVisionAnalysisService Service, OpenAiCompatibleCloudVisionAnalyzer Analyzer, CloudVisionRunPolicy Policy, string ProviderLabel, bool IsLocal)? CreateCloudVisionService(string featureId = AiFeatureIds.PhotoAnalysis) => VisionFactory(featureId);
    public ISceneGroupSummarizer? CreateSceneGroupSummarizer() => SceneFactory();
    public IVideoTranscriptionService? CreateVideoTranscriptionService() => TranscriptionFactory();
    public FaceTrainingSubmissionService CreateTrainingSubmissionService() => TrainingFactory();
    public void QueueImmediateRemoteSync(string reason) => PublishChanges(reason);
    public Task SyncRemoteMirrorAsync(CancellationToken cancellationToken = default, bool requireMetadataSync = false) => RefreshMirror(cancellationToken, requireMetadataSync);
    public Task SyncRemoteVideosAsync(CancellationToken cancellationToken = default) => RefreshVideos(cancellationToken);
    public Task<IndexSyncClient.IndexSyncResult?> SyncLocalChangesToRemoteAsync(CancellationToken cancellationToken = default, IProgress<string>? progress = null) => PushChanges(cancellationToken, progress);
}
