namespace Picer.Core.Classification;

[Flags]
public enum ClassificationUpdateReason
{
    None = 0, NewMedia = 1, MediaChanged = 2, DescriptionChanged = 4, PeopleChanged = 8,
    MetadataChanged = 16, ModelChanged = 32, PromptChanged = 64, TaxonomyChanged = 128,
    CandidatePolicyChanged = 256, RetryDue = 512, Interrupted = 1024
}
public enum ClassificationPlanAction { Skip, Enqueue, DeferRetry, AlreadyLeased, QueueSuccessor }
public sealed record ClassificationAttempt(string InputFingerprint, DateTimeOffset? RetryAfter, bool Retryable);
public sealed record ClassificationIncrementalDecision(ClassificationPlanAction Action,
    ClassificationUpdateReason Reasons, string DesiredFingerprint);

/// <summary>Pure planning decision only. Lease acquisition and publishing require the future durable CAS layer.</summary>
public static class ClassificationIncrementalPlanner
{
    public static ClassificationIncrementalDecision Decide(ClassificationInput current, ClassificationInput? lastSuccess,
        ClassificationAttempt? failedAttempt = null, string? leasedFingerprint = null, bool interrupted = false,
        DateTimeOffset? now = null)
    {
        var desired = ClassificationInputFingerprint.Compute(current);
        var reasons = Compare(current, lastSuccess, desired);
        if (interrupted) reasons |= ClassificationUpdateReason.Interrupted;
        if (leasedFingerprint is not null)
            return new(leasedFingerprint == desired ? ClassificationPlanAction.AlreadyLeased : ClassificationPlanAction.QueueSuccessor,
                reasons, desired);
        // A failed obsolete attempt must not delay a newer input.
        if (failedAttempt?.InputFingerprint == desired)
        {
            if (!failedAttempt.Retryable || failedAttempt.RetryAfter is { } after && after > (now ?? DateTimeOffset.UtcNow))
                return new(ClassificationPlanAction.DeferRetry, reasons, desired);
            reasons |= ClassificationUpdateReason.RetryDue;
        }
        return new(reasons == ClassificationUpdateReason.None ? ClassificationPlanAction.Skip : ClassificationPlanAction.Enqueue,
            reasons, desired);
    }

    private static ClassificationUpdateReason Compare(ClassificationInput current, ClassificationInput? previous, string desired)
    {
        if (previous is null) return ClassificationUpdateReason.NewMedia;
        // Validate both structures even when only a single field differs.
        if (ClassificationInputFingerprint.Compute(previous) == desired) return ClassificationUpdateReason.None;
        var reason = ClassificationUpdateReason.None;
        if (current.SpaceEpoch != previous.SpaceEpoch || current.OwnerId != previous.OwnerId
            || ClassificationInputFingerprint.NormalizeMedia(current.MediaIdentity) != ClassificationInputFingerprint.NormalizeMedia(previous.MediaIdentity))
            reason |= ClassificationUpdateReason.MediaChanged;
        if (ClassificationInputFingerprint.Compute(current with { Descriptions = previous.Descriptions }) != desired) reason |= ClassificationUpdateReason.DescriptionChanged;
        if (ClassificationInputFingerprint.Compute(current with { People = previous.People }) != desired) reason |= ClassificationUpdateReason.PeopleChanged;
        if (ClassificationInputFingerprint.Compute(current with { Metadata = previous.Metadata }) != desired) reason |= ClassificationUpdateReason.MetadataChanged;
        if (current.ModelVersion != previous.ModelVersion || current.PreprocessingVersion != previous.PreprocessingVersion) reason |= ClassificationUpdateReason.ModelChanged;
        if (current.PromptVersion != previous.PromptVersion) reason |= ClassificationUpdateReason.PromptChanged;
        if (current.DiscoveryRevision != previous.DiscoveryRevision
            || ClassificationInputFingerprint.Compute(current with { Definitions = previous.Definitions }) != desired) reason |= ClassificationUpdateReason.TaxonomyChanged;
        if (current.CandidatePolicyVersion != previous.CandidatePolicyVersion) reason |= ClassificationUpdateReason.CandidatePolicyChanged;
        return reason;
    }

}
