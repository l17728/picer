using Picer.Core.Models;

namespace Picer.Core.Vision;

public sealed record NormalizedFaceBox(float X, float Y, float Width, float Height);

public sealed record FaceDetection(
    NormalizedFaceBox Box,
    float Confidence,
    IReadOnlyList<float>? Landmarks = null,
    IReadOnlyList<float>? Embedding = null);

public sealed record FaceAnalysisResult(IReadOnlyList<FaceDetection> Faces);

public sealed record VisionAnalysisRecord(
    long ImageId,
    string Analyzer,
    string ModelVersion,
    long SourceFileSize,
    DateTime SourceModifiedUtc,
    string Status,
    int FaceCount,
    string? ErrorCode,
    DateTime AnalyzedUtc);

public interface IImageFaceAnalyzer
{
    string AnalyzerName { get; }
    string ModelVersion { get; }
    Task<FaceAnalysisResult> AnalyzeAsync(ImageRecord image, CancellationToken cancellationToken);
}

public sealed record VisionBatchResult(int Requested, int Analyzed, int Skipped, int Failed);

public sealed record UnassignedFaceRecord(
    long FaceId,
    long ImageId,
    string ImagePath,
    string? ThumbnailPath,
    int Ordinal,
    NormalizedFaceBox Box,
    float Confidence,
    long? SuggestedPersonId = null,
    string? SuggestedPersonName = null,
    string? AssignmentState = null);

public sealed record FaceEmbeddingRecord(long FaceId, long ImageId, string ModelVersion, long? PersonId,
    string? AssignmentState, float[] Embedding, float DetectionConfidence = 1, float RelativeArea = 1);
public sealed record RejectedFaceEmbeddingRecord(long FaceId, long ImageId, string ModelVersion, long PersonId,
    float[] Embedding, float DetectionConfidence = 1, float RelativeArea = 1);
public sealed record VideoTrainingFaceRecord(long VideoFaceId, long VideoId, long PersonId, string Label,
    float[] Embedding, float DetectionConfidence = 1, float RelativeArea = 1);
public sealed record FaceLearnedPolicy(long PersonId, string ArtifactId, string ModelVersion, float[] Prototype, double MatchThreshold);
public sealed record FaceTrainingArtifactSummary(string ArtifactId, string DatasetId, string ModelVersion, double FalseAcceptRate, double FalseRejectRate, string State, DateTime CreatedUtc, DateTime? AppliedUtc);
public sealed record FaceWorkQueueCounts(int Total, int Suggested, int Unassigned);
public sealed record CandidateClusterDraft(IReadOnlyList<(long FaceId, double Similarity)> Members);
public sealed record FaceCandidateClusterRecord(long Id, string ModelVersion, IReadOnlyList<UnassignedFaceRecord> Faces);
public sealed record SuggestedPersonGroupRecord(long PersonId, string PersonName, IReadOnlyList<UnassignedFaceRecord> Faces);
public sealed record ImageFaceRecord(long FaceId, int Ordinal, NormalizedFaceBox Box, float Confidence,
    long? PersonId, string? PersonName, string? AssignmentState);

public sealed record VisionAnalysisSummary(int TotalImages, int CompletedImages, int FailedImages, int FaceCount);
