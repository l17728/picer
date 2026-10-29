using Picer.Core.Models;
using Picer.Core.Search;
using System.Text.Json.Serialization;

namespace Picer.Core.Gateway;

public sealed record GatewayFaceReviewDecision(long FaceId, string State, string UpdatedUtc);

public sealed record GatewayAlbumImageLink(long AlbumId, long ImageId);
public sealed record GatewayImageTagLink(long ImageId, long TagId);
public sealed record GatewayDetectedFace(
    long Id, long ImageId, string ModelVersion, int Ordinal,
    double BoxX, double BoxY, double BoxWidth, double BoxHeight,
    double Confidence, string? LandmarksJson, byte[]? Embedding, string CreatedUtc, bool IsArchived = false);
public sealed record GatewayFaceAssignment(
    long FaceId, long PersonId, string State, double? Confidence, string AssignedBy, string UpdatedUtc);
public sealed record GatewayVideoKeyframe(long Id, long VideoId, double TimestampSeconds, string Path, long DifferenceHash);
public sealed record GatewayVideoTranscript(long VideoId, string ModelVersion, string TranscriptJson)
{
    public string? UpdatedUtc { get; init; }
}
public sealed record GatewayVideoSummary(long VideoId, string Provider, string ModelVersion, string PromptVersion, string Summary, string ResultJson,
    long? SourceFileSize = null, string? SourceModifiedUtc = null)
{
    public string? UpdatedUtc { get; init; }
}
public sealed record GatewayVideoFace(long Id, long VideoId, long KeyframeId, double TimestampSeconds, int Ordinal,
    double BoxX, double BoxY, double BoxWidth, double BoxHeight, double Confidence, byte[]? Embedding,
    long? SuggestedPersonId, double? MatchConfidence, long? AssignedPersonId, string? AssignmentState,
    string? UpdatedUtc, string CreatedUtc)
{
    public string ModelVersion { get; init; } = "";
}
public sealed record GatewayCloudVisionAnalysis(long ImageId, string Provider, string ModelVersion, string PromptVersion,
    string Status, string? Summary, string? ResultJson, string UpdatedUtc,
    long? SourceFileSize = null, string? SourceModifiedUtc = null);
public sealed record GatewaySceneGroupDescription(string Fingerprint, string DirectoryPath, string StartUtc, string EndUtc,
    string ImageIdsJson, string RepresentativeImageIdsJson, string Provider, string ModelVersion, string PromptVersion,
    string SceneName, string Summary, string SearchTermsJson, string State, string CreatedUtc, string ReviewedUtc);
public sealed record GatewayFaceReviewState(long FaceId, string State, string UpdatedUtc);
public sealed record GatewayFaceCandidateCluster(long Id, string ModelVersion, string CreatedUtc);
public sealed record GatewayFaceCandidateMember(long ClusterId, long FaceId, double Similarity);
public sealed record GatewayFaceTrainingArtifact(string ArtifactId, string DatasetId, string Endpoint, string ModelVersion,
    string ArtifactJson, double FalseAcceptRate, double FalseRejectRate, string State, string CreatedUtc, string? AppliedUtc);
public sealed record GatewayFacePersonPrototype(long PersonId, byte[] Centroid, double Threshold, int SampleCount,
    string ModelVersion, string UpdatedUtc);
public sealed record GatewayFacePersonPolicy(long PersonId, string ArtifactId, string ModelVersion, byte[] Prototype,
    double MatchThreshold, string UpdatedUtc);
public sealed record GatewayFaceModelSetting(string ModelVersion, double MatchThreshold, int CalibratedSamePairs,
    int CalibratedDifferentPairs, string UpdatedUtc);
public sealed record GatewayClassificationAssertion(long ImageId, string? ContentSha256, string ConceptId,
    string SourceKind, string SourceVersion, string AssertionState, string ReviewState, float? Confidence,
    string? EvidenceFingerprint, string CreatedUtc, string UpdatedUtc, string? ReviewedUtc);
public sealed record GatewayClassificationRejection(long ImageId, string ConceptId, string SourceScope, string RejectedUtc);
public sealed record GatewaySemanticContentCluster(string ClusterKey, string ModelVersion, string DisplayName,
    float Confidence, int MemberCount, string CreatedUtc, string UpdatedUtc);
public sealed record GatewaySemanticContentClusterMember(string ClusterKey, long ImageId, float Similarity);
[method: JsonConstructor]
public sealed record GatewayMetadata(
    IReadOnlyList<GatewayAlbumImageLink> AlbumImages,
    IReadOnlyList<GatewayImageTagLink> ImageTags,
    IReadOnlyList<ImageLocationHierarchyRow> Locations,
    IReadOnlyList<GatewayDetectedFace> Faces,
    IReadOnlyList<GatewayFaceAssignment> FaceAssignments,
    IReadOnlyList<GatewayVideoKeyframe> VideoKeyframes,
    IReadOnlyList<GatewayVideoTranscript> VideoTranscripts,
    IReadOnlyList<GatewayVideoSummary> VideoSummaries,
    IReadOnlyList<GatewayVideoFace>? VideoFaces = null,
    IReadOnlyList<GatewayCloudVisionAnalysis>? CloudVisionAnalyses = null,
    IReadOnlyList<GatewaySceneGroupDescription>? SceneGroupDescriptions = null,
    IReadOnlyList<GatewayFaceReviewState>? FaceReviewStates = null,
    IReadOnlyList<GatewayFaceCandidateCluster>? FaceCandidateClusters = null,
    IReadOnlyList<GatewayFaceCandidateMember>? FaceCandidateMembers = null,
    IReadOnlyList<GatewayFaceTrainingArtifact>? FaceTrainingArtifacts = null,
    IReadOnlyList<GatewayFacePersonPrototype>? FacePersonPrototypes = null,
    IReadOnlyList<GatewayFacePersonPolicy>? FacePersonPolicies = null,
    IReadOnlyList<GatewayFaceModelSetting>? FaceModelSettings = null,
    IReadOnlyList<GatewayFaceReviewDecision>? FaceReviewDecisions = null,
    IReadOnlyList<GatewayClassificationAssertion>? ClassificationAssertions = null,
    IReadOnlyList<GatewayClassificationRejection>? ClassificationRejections = null,
    IReadOnlyList<GatewaySemanticContentCluster>? SemanticContentClusters = null,
    IReadOnlyList<GatewaySemanticContentClusterMember>? SemanticContentClusterMembers = null,
    IReadOnlyList<DescriptionVersion>? DescriptionVersions = null)
{
    public GatewayMetadata(
        IReadOnlyList<GatewayAlbumImageLink> albumImages,
        IReadOnlyList<GatewayImageTagLink> imageTags,
        IReadOnlyList<ImageLocationHierarchyRow> locations)
        : this(albumImages, imageTags, locations, [], [], [], [], [], []) { }

    [JsonIgnore] public IReadOnlyList<GatewayVideoFace> VideoFacesOrEmpty => VideoFaces ?? [];
    [JsonIgnore] public IReadOnlyList<GatewayCloudVisionAnalysis> CloudVisionAnalysesOrEmpty => CloudVisionAnalyses ?? [];
    [JsonIgnore] public IReadOnlyList<GatewaySceneGroupDescription> SceneGroupDescriptionsOrEmpty => SceneGroupDescriptions ?? [];
    [JsonIgnore] public IReadOnlyList<GatewayFaceReviewState> FaceReviewStatesOrEmpty => FaceReviewStates ?? [];
    [JsonIgnore] public IReadOnlyList<GatewayFaceCandidateCluster> FaceCandidateClustersOrEmpty => FaceCandidateClusters ?? [];
    [JsonIgnore] public IReadOnlyList<GatewayFaceCandidateMember> FaceCandidateMembersOrEmpty => FaceCandidateMembers ?? [];
    [JsonIgnore] public IReadOnlyList<GatewayFaceTrainingArtifact> FaceTrainingArtifactsOrEmpty => FaceTrainingArtifacts ?? [];
    [JsonIgnore] public IReadOnlyList<GatewayFacePersonPrototype> FacePersonPrototypesOrEmpty => FacePersonPrototypes ?? [];
    [JsonIgnore] public IReadOnlyList<GatewayFacePersonPolicy> FacePersonPoliciesOrEmpty => FacePersonPolicies ?? [];
    [JsonIgnore] public IReadOnlyList<GatewayFaceModelSetting> FaceModelSettingsOrEmpty => FaceModelSettings ?? [];
    [JsonIgnore] public IReadOnlyList<GatewayClassificationAssertion> ClassificationAssertionsOrEmpty => ClassificationAssertions ?? [];
    [JsonIgnore] public IReadOnlyList<GatewayClassificationRejection> ClassificationRejectionsOrEmpty => ClassificationRejections ?? [];
    [JsonIgnore] public IReadOnlyList<GatewaySemanticContentCluster> SemanticContentClustersOrEmpty => SemanticContentClusters ?? [];
    [JsonIgnore] public IReadOnlyList<GatewaySemanticContentClusterMember> SemanticContentClusterMembersOrEmpty => SemanticContentClusterMembers ?? [];
}
