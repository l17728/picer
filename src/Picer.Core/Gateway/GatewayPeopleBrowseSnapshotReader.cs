using Picer.Core.Data;
using Picer.Core.People;
using Picer.Core.Vision;

namespace Picer.Core.Gateway;

public static class GatewayPeopleBrowseSnapshotReader
{
    public static GatewayPeopleBrowseSnapshot Read(PicerDatabase database, string view, string? modelVersion,
        long? personId, int limit)
    {
        var repository = new VisionAnalysisRepository(database);
        var normalizedView = view.Trim().ToLowerInvariant();
        var boundedLimit = Math.Clamp(limit, 1, 5000);
        IReadOnlyList<UnassignedFaceRecord> faces = normalizedView switch
        {
            "pending" => repository.GetUnassignedFaces(boundedLimit, modelVersion),
            "ignored" => repository.GetIgnoredFaces(boundedLimit, modelVersion),
            "person" when personId is > 0 => repository.GetPersonFaces(personId.Value, boundedLimit),
            "summary" => [],
            _ => throw new ArgumentOutOfRangeException(nameof(view), view, "Unsupported people browse view.")
        };

        var includeWorkflowLists = normalizedView is "pending" or "summary";
        var includeCounts = normalizedView is "pending" or "summary";
        return new GatewayPeopleBrowseSnapshot(
            new PeopleService(database).GetAll(),
            faces,
            includeWorkflowLists ? repository.GetCandidateClusters(modelVersion) : [],
            includeWorkflowLists ? repository.GetSuggestedPersonGroups(modelVersion) : [],
            includeCounts ? repository.GetFaceWorkQueueCounts(modelVersion) : new FaceWorkQueueCounts(0, 0, 0, 0),
            includeCounts && !string.IsNullOrWhiteSpace(modelVersion)
                ? repository.GetFacePartitionCounts(modelVersion)
                : new FacePartitionCounts(0, 0, 0, 0, 0, 0),
            includeCounts ? repository.GetIgnoredFaceCount() : 0,
            includeCounts ? repository.GetSummary() : new VisionAnalysisSummary(0, 0, 0, 0),
            normalizedView == "summary" && !string.IsNullOrWhiteSpace(modelVersion)
                ? repository.GetFaceTrainingHistory(modelVersion)
                : []);
    }
}
