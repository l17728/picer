using System.Text.Json;
using Picer.Core.Models;
using Picer.Core.Video;
using Picer.Core.Search;
using Picer.Core.People;
using Picer.Core.Gateway;

namespace Picer.Core.Identity;

public static class SpaceSyncProtocol
{
    public const int MetadataVersion=14;
    public static void RequireMetadataSupport(SpacePublisher publisher)
    {
        if (publisher.MetadataProtocol!=MetadataVersion)
            throw new InvalidOperationException("照片空间服务不支持当前元数据同步协议，请将 IndexService 与 GUI 一同升级后重试。");
    }
}

public sealed record SpaceSourceBindingRequest(string PublisherId, string AgentGrantId, IReadOnlyList<long> LocalSourceIds);
public sealed record SpaceInspectionRequest(string OperationId);
public sealed record SpaceInspectionTarget(string TargetId, string PublisherId, string DeviceId, string State,
    string UpdatedUtc, string? ErrorCode, string? DeviceName = null, string? LastSuccessUtc = null);
public sealed record SpaceInspectionJob(string JobId, string State, IReadOnlyList<SpaceInspectionTarget> Targets,
    long PendingOwnershipReviews = 0, long PendingMetadataConflicts = 0);
public sealed record SpaceInspectionJobRequest(string JobId);
public sealed record SpaceArchivedOperationRequest(string PublisherId, string Epoch, IReadOnlyList<string> OperationIds);
public sealed record SpaceArchivedOperationResponse(string PublisherId, string Epoch, IReadOnlyList<string> OperationIds);
public sealed record SpaceConflictResolutionResponse(string PublisherId, string Epoch, IReadOnlyList<Picer.Core.Data.MetadataConflictReceipt> Resolutions);
public sealed record SpaceMetadataOperationState(string OperationId, Picer.Core.Data.MetadataEdit Current);
public sealed record SpaceMetadataOperationResponse(string PublisherId, string Epoch, IReadOnlyList<SpaceMetadataOperationState> Operations);
public sealed record SpaceInspectionReceipt(string TargetId, string PublisherId, string Epoch, string Result, string? ErrorCode = null);
public sealed record SpaceNotificationRequest(string? LibraryId, long After = -1, string? Epoch = null, int WaitSeconds = 25);
public sealed record SpaceNotification(string? PublisherId, string Epoch, long Revision, IReadOnlyList<string> InspectionTargets);

public sealed record SpacePublisherRequest(string LibraryId, string IncarnationId);
public sealed record SpaceIdentityOfferRequest(string PublisherId, string Epoch, SpaceUploadScope Scope, int MetadataProtocol = 0);
public sealed record SpaceIdentityOffer(string OfferId, string Kind, string MediaGlobalId, JsonElement Data, string MediaKind = "image");
public sealed record SpaceVideoFaceIdentityData(string KeyframeGlobalId, GatewayVideoFace Face);
public sealed record SpaceIdentityOfferPage(string PublisherId, string Epoch, IReadOnlyList<SpaceIdentityOffer> Offers, bool HasMore);
public sealed record SpaceCatalogIdentityData(string Name, string CreatedUtc, string? Description = null);
public sealed record SpaceIdentityAdoption(string OfferId, string Kind, long LocalId, string GlobalId);
public sealed record SpaceIdentityAdoptionRequest(string PublisherId, string Epoch, SpaceUploadScope Scope,
    IReadOnlyList<SpaceIdentityAdoption> Items);
public sealed record SpaceIdentityAdoptionReceipt(string PublisherId, string Epoch, IReadOnlyList<SpaceIdentityAdoption> Items,
    IReadOnlyList<SpaceIdentityAdoption>? Deferred = null);
public sealed record SpaceRebaselineRequest(string LibraryId, string IncarnationId, string? Epoch = null,
    string AfterKind = "", string AfterKey = "", bool Complete = false);
public sealed record SpaceRebaselinePage(SpacePublisher Publisher, IReadOnlyList<SpaceEntityIdentity> Identities,
    bool HasMore, string AfterKind, string AfterKey);
public sealed record SpacePublisher(string PublisherId, string LibraryId, string IncarnationId, string Epoch, long LastSequence, int MetadataProtocol = 0);
public sealed record SpaceEntityIdentity(string Kind, long LocalId, string GlobalId);
public sealed record SpaceMediaReferenceRequest(string PublisherId,string Epoch,SpaceUploadScope Scope,string Kind,string GlobalId);
public sealed record SpaceMediaReference(long Id,string MirrorEpoch);
public sealed record SpaceSyncBatch(string PublisherId, string Epoch, long Sequence, string OperationId,
    string Kind, JsonElement Payload, IReadOnlyList<SpaceEntityIdentity> Identities)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SpaceEntityIdentity>? CatalogReferences { get; init; }
}
public sealed record SpaceSyncReceipt(long Sequence, string OperationId, int Status, string Code, int Accepted);
public sealed record SpaceImageBatch(long SourceId, string SourcePath, IReadOnlyList<ImageRecord> Images,
    IReadOnlyDictionary<long,string>? ContentIdentities=null);
public sealed record SpaceVideoBatch(long SourceId, string SourcePath, IReadOnlyList<VideoRecord> Videos,
    IReadOnlyDictionary<long, string>? ContentIdentities = null);
public sealed record SpaceSourceBatch(long SourceId, string SourcePath);
public sealed record SpaceCatalogBatch(IReadOnlyList<Tag> Tags, IReadOnlyList<Album> Albums, IReadOnlyList<PersonSummary> People);
public sealed record SpaceMetadataBatch(GatewayMetadata Metadata);
public sealed record SpaceMetadataEditsBatch(IReadOnlyList<Picer.Core.Data.MetadataEdit> Edits);
public sealed record SpaceMetadataEditSnapshot(IReadOnlyList<Picer.Core.Data.MetadataEdit> Edits, string? PublisherId = null, string? Epoch = null);
public sealed record SpaceMetadataEditPage(string PublisherId,string Epoch,long Through,long NextVersion,string NextOperation,
    bool HasMore,IReadOnlyList<Picer.Core.Data.MetadataEdit> Edits,long Latest);
public sealed record SpaceSceneDescription(GatewaySceneGroupDescription Scene,string RemoteFingerprint);
public sealed record SpaceMetadataChange(long ChangeId, string ImageGlobalId,
    IReadOnlyList<GatewayClassificationAssertion> Assertions,
    IReadOnlyList<GatewayClassificationRejection> Rejections,
    IReadOnlyList<GatewaySemanticContentCluster> Clusters,
    IReadOnlyList<GatewaySemanticContentClusterMember> ClusterMembers,
    string MediaKind = "image",
    IReadOnlyList<GatewayCloudVisionAnalysis>? PhotoAnalyses = null,
    IReadOnlyList<GatewayVideoSummary>? VideoSummaries = null,
    IReadOnlyList<GatewayVideoTranscript>? VideoTranscripts = null,
    IReadOnlyList<GatewayDetectedFace>? Faces = null,
    IReadOnlyList<GatewayFaceAssignment>? FaceAssignments = null,
    IReadOnlyList<GatewayFaceReviewDecision>? FaceReviewDecisions = null,
    IReadOnlyList<GatewayVideoKeyframe>? VideoKeyframes = null,
    IReadOnlyList<GatewayVideoFace>? VideoFaces = null,
    IReadOnlyList<ImageLocationHierarchyRow>? Locations = null,
    IReadOnlyList<GatewayFaceCandidateCluster>? FaceCandidateClusters = null,
    IReadOnlyList<GatewayFaceCandidateMember>? FaceCandidateMembers = null,
    IReadOnlyList<SpaceSceneDescription>? SceneDescriptions = null);
public sealed record SpaceMetadataChangePage(long Through, bool HasMore,
    IReadOnlyList<SpaceMetadataChange> Changes, string? PublisherId = null, string? Epoch = null);
public sealed record SpaceUploadScope(IReadOnlyList<long> SourceIds, bool Photos = true, bool Videos = true,
    bool People = false, bool PreciseLocation = false);

public sealed record SpaceCatalogRetirementRequest(string PublisherId,string Epoch,SpaceUploadScope Scope,
    IReadOnlyList<Picer.Core.Data.CatalogRetirementAnchor> Anchors);
public sealed record SpaceCatalogRetirementItemResult(string OperationId,string MediaKind,string MediaGlobalId,
    Picer.Core.Data.CatalogRetirementApplyResult Result);
public sealed record SpaceCatalogRetirementResponse(string PublisherId,string Epoch,IReadOnlyList<SpaceCatalogRetirementItemResult> Results);
public sealed record SpaceCatalogRetirementReadRequest(string PublisherId,string Epoch,SpaceUploadScope Scope,
    Picer.Core.Data.CatalogRetirementCursor? After=null,int PageSize=200);
public sealed record SpaceCatalogRetirementPage(string PublisherId,string Epoch,
    IReadOnlyList<Picer.Core.Data.CatalogRetirementAnchor> Anchors,Picer.Core.Data.CatalogRetirementCursor? Next,bool HasMore);
public sealed record SpaceCatalogRetirementDelivery(Picer.Core.Data.CatalogRetirementAnchor Anchor,
    Picer.Core.Data.CatalogRetirementApplyResult Result);
public sealed record SpaceCatalogRetirementDeliveryRequest(string PublisherId,string Epoch,SpaceUploadScope Scope,
    IReadOnlyList<SpaceCatalogRetirementDelivery> Items);
public sealed record SpaceCatalogRetirementDeliveryResponse(string PublisherId,string Epoch,
    IReadOnlyList<Picer.Core.Data.CatalogRetirementCursor> Accepted);

public sealed record SpaceCatalogCompletionReadRequest(string PublisherId,string Epoch,SpaceUploadScope Scope,string After="",int PageSize=100);
public sealed record SpaceCatalogCompletionPage(string PublisherId,string Epoch,
    IReadOnlyList<Picer.Core.Data.CatalogRetirementManifest> Items,string Next,bool HasMore);
public sealed record SpaceCatalogCompletionRequest(string PublisherId,string Epoch,SpaceUploadScope Scope,
    IReadOnlyList<Picer.Core.Data.CatalogRetirementManifest> Items);
public sealed record SpaceCatalogCompletionItemResult(string OperationId,Picer.Core.Data.CatalogRetirementCompletion Result);
public sealed record SpaceCatalogCompletionResponse(string PublisherId,string Epoch,IReadOnlyList<SpaceCatalogCompletionItemResult> Results);
