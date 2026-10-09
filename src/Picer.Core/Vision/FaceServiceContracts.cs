namespace Picer.Core.Vision;

public sealed record FaceServiceResult(
    string SchemaVersion,
    string Operation,
    string ModelVersion,
    IReadOnlyList<FaceServiceFace> Faces,
    string? VideoId = null,
    string? Error = null);

public sealed record FaceServiceFace(
    int Ordinal,
    double? TimestampSeconds,
    float X,
    float Y,
    float Width,
    float Height,
    float Confidence,
    float[]? Embedding = null,
    long? KeyframeId = null,
    long? SuggestedPersonId = null,
    double? MatchConfidence = null);

public static class FaceServiceContract
{
    public const string CurrentSchema = "picer.face.v1";

    public static bool IsValid(FaceServiceResult? result, string operation)
    {
        if (result is null || !string.Equals(result.SchemaVersion, CurrentSchema, StringComparison.Ordinal) ||
            !string.Equals(result.Operation, operation, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(result.ModelVersion)) return false;
        return result.Faces.All(face => face.Ordinal >= 0 && face.X >= 0 && face.Y >= 0 && face.Width > 0 && face.Height > 0 &&
            face.X + face.Width <= 1.001f && face.Y + face.Height <= 1.001f && face.Confidence is >= 0 and <= 1 &&
            (face.Embedding is null || face.Embedding.Length is > 0 and <= 4096));
    }
}
