namespace Picer.Core.People;

/// Immutable audit event used as training evidence; it never replaces the
/// current assignment state and is safe to replay when rebuilding a dataset.
public sealed record FaceUserActionEvent(long FaceId, long? PersonId, string Action,
    DateTime OccurredUtc, string Source = "user", string? CorrelationId = null);
