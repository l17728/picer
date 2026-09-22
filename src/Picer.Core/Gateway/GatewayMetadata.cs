using Picer.Core.Models;
using Picer.Core.Search;
using System.Text.Json.Serialization;

namespace Picer.Core.Gateway;

public sealed record GatewayAlbumImageLink(long AlbumId, long ImageId);
public sealed record GatewayImageTagLink(long ImageId, long TagId);
public sealed record GatewayDetectedFace(
    long Id, long ImageId, string ModelVersion, int Ordinal,
    double BoxX, double BoxY, double BoxWidth, double BoxHeight,
    double Confidence, string? LandmarksJson, byte[]? Embedding, string CreatedUtc);
public sealed record GatewayFaceAssignment(
    long FaceId, long PersonId, string State, double? Confidence, string AssignedBy, string UpdatedUtc);
public sealed record GatewayVideoKeyframe(long Id, long VideoId, double TimestampSeconds, string Path, long DifferenceHash);
public sealed record GatewayVideoTranscript(long VideoId, string ModelVersion, string TranscriptJson);
public sealed record GatewayVideoSummary(long VideoId, string Provider, string ModelVersion, string PromptVersion, string Summary, string ResultJson);
public sealed record GatewayVideoFace(long Id, long VideoId, long KeyframeId, double TimestampSeconds, int Ordinal,
    double BoxX, double BoxY, double BoxWidth, double BoxHeight, double Confidence, byte[]? Embedding,
    long? SuggestedPersonId, double? MatchConfidence, long? AssignedPersonId, string? AssignmentState,
    string? UpdatedUtc, string CreatedUtc);
public sealed record GatewayCloudVisionAnalysis(long ImageId, string Provider, string ModelVersion, string PromptVersion,
    string Status, string? Summary, string? ResultJson, string UpdatedUtc);
public sealed record GatewaySceneGroupDescription(string Fingerprint, string DirectoryPath, string StartUtc, string EndUtc,
    string ImageIdsJson, string RepresentativeImageIdsJson, string Provider, string ModelVersion, string PromptVersion,
    string SceneName, string Summary, string SearchTermsJson, string State, string CreatedUtc, string ReviewedUtc);
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
    IReadOnlyList<GatewaySceneGroupDescription>? SceneGroupDescriptions = null)
{
    public GatewayMetadata(
        IReadOnlyList<GatewayAlbumImageLink> albumImages,
        IReadOnlyList<GatewayImageTagLink> imageTags,
        IReadOnlyList<ImageLocationHierarchyRow> locations)
        : this(albumImages, imageTags, locations, [], [], [], [], [], []) { }

    public IReadOnlyList<GatewayVideoFace> VideoFacesOrEmpty => VideoFaces ?? [];
    public IReadOnlyList<GatewayCloudVisionAnalysis> CloudVisionAnalysesOrEmpty => CloudVisionAnalyses ?? [];
    public IReadOnlyList<GatewaySceneGroupDescription> SceneGroupDescriptionsOrEmpty => SceneGroupDescriptions ?? [];
}
