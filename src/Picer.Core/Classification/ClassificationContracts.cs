using Picer.Core.Models;

namespace Picer.Core.Classification;

public sealed record ClassificationConfiguration(bool Enabled, string Model, string Endpoint,
    bool AllowRemote, int MaximumCalls = 100, int MaximumOutputTokens = 8192,
    string PromptVersion = "family-classifier-v2", string PreprocessingVersion = "verified-jpeg-1280-v1", string? ModelAgentId = null);
public sealed record ClassificationTaxonomySnapshot(long Revision, long DiscoveryRevision,
    IReadOnlyList<ClassificationLabel> Labels, IReadOnlyList<ClassificationEdge> Edges,
    IReadOnlyList<ClassificationDefinitionVersion> Definitions);
public sealed record ClassificationContext(ImageRecord Image, ClassificationInput Input,
    IReadOnlyDictionary<string, string> Evidence, string? Actor = null)
{
    public bool RetryIncompleteOutput { get; init; }
    public bool ReviewEmpty { get; init; }
    public int JobCallNumber { get; init; }
}
public sealed record ClassificationLabelEvaluation(string LabelId, string Support, IReadOnlyList<string> EvidenceIds);
public sealed record ClassificationResult(string SchemaVersion, string InputFingerprint,
    IReadOnlyList<ClassificationLabelEvaluation> Labels, IReadOnlyList<ClassificationProposalDraft> Proposals)
{
    public bool VisuallyReviewed { get; init; }
    public bool PrimarySubjectReviewed { get; init; }
}
public sealed record ClassificationProposalDraft(string Name, string Dimension, string Definition, string Exclusions,
    IReadOnlyList<string> ParentIds, IReadOnlyList<string> EvidenceIds);
public sealed record ClassificationPlanItem(long ImageId, string Fingerprint, ClassificationInput? Input, ClassificationUpdateReason Reasons);
public sealed record ClassificationPlan(string Id, string RequestId, string Epoch, long TaxonomyRevision,
    DateTimeOffset ExpiresAt, ClassificationConfiguration Configuration, int Total, int Unchanged,
    IReadOnlyList<ClassificationPlanItem> Items, long InputRevision = 0, int Deferred = 0);
public sealed record ClassificationPlanPreview(string Id, int Total, int Unchanged, int Pending,
    ClassificationConfiguration Configuration, IReadOnlyDictionary<ClassificationUpdateReason, int> Reasons)
{
    public int Deferred { get; init; }
    public static ClassificationPlanPreview From(ClassificationPlan plan) => new(plan.Id, plan.Total, plan.Unchanged, plan.Items.Count,
        plan.Configuration, Enum.GetValues<ClassificationUpdateReason>().Where(r => r != ClassificationUpdateReason.None)
            .ToDictionary(r => r, r => plan.Items.Count(item => item.Reasons.HasFlag(r)))) { Deferred = plan.Deferred };
}
public sealed record ClassificationJob(string Id, string State, int Total, int Succeeded, int Failed, int Pending,
    int Running, int UsedCalls, int MaximumCalls);
public sealed record ClassificationLease(string JobId, long ImageId, string Token, string Fingerprint, ClassificationInput Input, string? Actor = null)
{
    public bool RetryIncompleteOutput { get; init; }
    public bool ReviewEmpty { get; init; }
    public int JobCallNumber { get; init; }
}
public sealed record ClassificationCoverage(int Total, int Confirmed, int Suggested, int Legacy, int WithoutContent,
    int Unprocessed, int PendingUpdate, int Failed, int AnalyzedUnclassified, int WithoutDescription, int WithPeople,
    int LegacySuggested = 0, int EvidenceDerived = 0);
public sealed record ClassificationBrowseRequest(ClassificationFilter Filter, bool IncludeSuggestions = true,
    bool IncludeLegacy = true, int PageSize = 60, string? Cursor = null, string? Snapshot = null);
public sealed record ClassificationPhoto(long ImageId, string Path, string? ThumbnailPath,
    IReadOnlyList<string> LabelIds, string State, string ContentIdentity = "", string OwnerId = "",
    IReadOnlyList<string>? ConfirmedIds = null, IReadOnlyList<string>? SuggestedIds = null, IReadOnlyList<string>? LegacyIds = null,
    IReadOnlyList<string>? ConflictLabelIds = null, string? ManualFingerprint = null,
    IReadOnlyList<string>? LegacySuggestedIds = null, IReadOnlyList<string>? EvidenceLabelIds = null);
public sealed record ClassificationDecision(string OperationId, string Epoch, string LabelId, string Decision,
    IReadOnlyDictionary<long, string> ContentIdentities, IReadOnlyDictionary<long, string> Owners,
    long ExpectedTaxonomyRevision = 0, IReadOnlyDictionary<long, string>? ManualFingerprints = null);
public sealed record ClassificationBrowseResult(string Snapshot, ClassificationTaxonomySnapshot Taxonomy,
    ClassificationCoverage Coverage, IReadOnlyDictionary<string, int> Counts, int Matched,
    IReadOnlyList<ClassificationPhoto> Photos, string? NextCursor, bool IsOffline = false)
{
    public string? Epoch { get; init; }
}
/// <summary>Besides one entry per label id, <see cref="ClassificationBrowseResult.Counts"/> carries aggregate
/// entries under these reserved keys. Aggregate counts are deduplicated: a photo with several labels of one
/// dimension counts once for that dimension, and once for the named-people group.</summary>
public static class ClassificationCountKeys
{
    public const string DimensionPrefix = "dimension:";
    public const string NamedPeople = "group:named-people";
    public static string Dimension(string dimension) => DimensionPrefix + dimension;
}
public sealed record ClassificationOutboxItem(string OperationId, string Route, string Payload, string State, long Sequence = 0);
public sealed record ClassificationProposal(string Id, string OwnerKey, long Revision, string State,
    ClassificationProposalDraft Draft, IReadOnlyList<long> ImageIds, IReadOnlyDictionary<long, string>? InputFingerprints = null);
public sealed record ClassificationProposalReview(string OperationId, string ProposalId, long ExpectedRevision,
    long ExpectedTaxonomyRevision, string Decision, string? MergeLabelId = null);
public sealed record ClassificationTaxonomyEdit(string OperationId, long ExpectedRevision, string LabelId,
    string? Name = null, IReadOnlyList<string>? BrowseParentIds = null, bool? Active = null,
    string? MergeInto = null, bool ConfirmEquivalent = false);
public sealed record ClassificationClusterStatus(string Id, string State, string? ErrorCode);
public sealed record ClassificationReplica(string Epoch, string Snapshot, ClassificationTaxonomySnapshot Taxonomy,
    IReadOnlyList<ClassificationPhoto> Photos, IReadOnlyList<ClassificationProposal> Proposals, ClassificationCoverage Coverage);

public interface IClassificationModel
{
    Task<ClassificationResult> ClassifyAsync(ClassificationContext context, ClassificationTaxonomySnapshot taxonomy,
        ClassificationConfiguration configuration, CancellationToken cancellationToken);
}
public sealed record ClassificationClusterRequest(string RequestId, IReadOnlyList<long> ImageIds);
public sealed record ClassificationClusterTask(string Id, string State, string Epoch, long TaxonomyRevision,
    ClassificationConfiguration Configuration, IReadOnlyList<ClassificationContext> Representatives, int CandidateCount, string? Actor = null);
public sealed record ClassificationClusterResult(string SchemaVersion, IReadOnlyList<long> EvaluatedImageIds,
    IReadOnlyList<ClassificationClusterProposal> Proposals);
public sealed record ClassificationClusterProposal(ClassificationProposalDraft Draft, IReadOnlyList<long> SupportedImageIds);
public interface IClassificationClusterModel
{
    Task<ClassificationClusterResult> ClusterAsync(ClassificationClusterTask task, ClassificationTaxonomySnapshot taxonomy,
        CancellationToken cancellationToken);
}
