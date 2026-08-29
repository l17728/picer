using Picer.Core.Models;

namespace Picer.Core.Vision;

public sealed record LocationLandmark(string Name, double? DistanceMeters = null);

public sealed record PhotoLocationContext(
    double? Latitude,
    double? Longitude,
    string? DisplayName,
    string? Country = null,
    string? Province = null,
    string? City = null,
    string? District = null,
    string? Poi = null,
    IReadOnlyList<LocationLandmark>? NearbyLandmarks = null,
    string Accuracy = "estimated");

public sealed record CloudVisionRequest(
    ImageRecord Image,
    PhotoLocationContext? Location,
    IReadOnlyList<VisualConceptScore> LocalConcepts,
    string? AdditionalContext = null);

public sealed record CloudVisionResult(
    string Summary,
    IReadOnlyList<string> Scenes,
    IReadOnlyList<string> Activities,
    IReadOnlyList<string> Objects,
    IReadOnlyList<string> SearchTerms,
    string? LocationDisplayName = null,
    string LocationSource = "none",
    CloudVisionUsage? Usage = null);

public sealed record CloudVisionUsage(long InputTokens, long OutputTokens, decimal EstimatedCostYuan);

public interface ICloudVisionAnalyzer
{
    string Provider { get; }
    string ModelVersion { get; }
    string PromptVersion { get; }
    Task<CloudVisionResult> AnalyzeAsync(CloudVisionRequest request, CancellationToken cancellationToken);
}

public sealed record CloudVisionBatchResult(int Requested, int Completed, int Skipped, int Failed, int BudgetBlocked = 0);
