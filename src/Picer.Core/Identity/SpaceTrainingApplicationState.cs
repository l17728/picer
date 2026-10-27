namespace Picer.Core.Identity;

// Only application receipts are returned. Signed documents and biometric data
// remain in the originating library; no other publisher's aggregates are copied.
public sealed record SpaceTrainingStateRequest(string PublisherId, string Epoch, SpaceUploadScope Scope,
    string AfterArtifact = "");
public sealed record SpaceTrainingApplicationState(string ArtifactId, string ModelVersion,
    string AppliedUtc, string DocumentDigest, bool Learned);
public sealed record SpaceTrainingStatePage(string PublisherId, string Epoch,
    IReadOnlyList<SpaceTrainingApplicationState> Applications, string AfterArtifact, bool HasMore);
