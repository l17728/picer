namespace Picer.App;

public static partial class AppServices
{
    private static readonly ClientServices ClientContext = new();
    public static ClientServices GetClientServices()
    {
        ClientContext.Gateway = Gateway;
        ClientContext.SyncClient = SyncClient;
        ClientContext.ServiceMode = ServiceMode;
        ClientContext.Db = Db;
        ClientContext.Storage = Storage;
        ClientContext.RemoteThumbnails = RemoteThumbnails;
        ClientContext.FileOperations = FileOperations;
        ClientContext.LocationHierarchy = LocationHierarchy;
        ClientContext.Config = Config;
        ClientContext.People = People;
        ClientContext.CloudVisionRepository = CloudVisionRepository;
        ClientContext.MediaSearch = MediaSearch;
        ClientContext.VisualConceptRepository = VisualConceptRepository;
        ClientContext.Videos = Videos;
        ClientContext.VideoIndex = VideoIndex;
        ClientContext.VideoKeyframes = VideoKeyframes;
        ClientContext.VideoTranscription = VideoTranscription;
        ClientContext.VideoFaceTimeline = VideoFaceTimeline;
        ClientContext.VideoSummaries = VideoSummaries;
        ClientContext.TrainingServiceEndpoint = TrainingServiceEndpoint;
        ClientContext.ChatFactory = CreateAiChatClient;
        ClientContext.VisionFactory = CreateCloudVisionService;
        ClientContext.SceneFactory = CreateSceneGroupSummarizer;
        ClientContext.TranscriptionFactory = CreateVideoTranscriptionService;
        ClientContext.TrainingFactory = CreateTrainingSubmissionService;
        ClientContext.PurgeVideo = PurgeVideoAsync;
        ClientContext.PublishChanges = QueueImmediateRemoteSync;
        ClientContext.RefreshMirror = (token, required) => SyncRemoteMirrorAsync(token, required);
        ClientContext.RefreshVideos = SyncRemoteVideosAsync;
        ClientContext.PushChanges = SyncLocalChangesToRemoteAsync;
        return ClientContext;
    }
}
