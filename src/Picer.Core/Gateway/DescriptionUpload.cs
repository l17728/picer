namespace Picer.Core.Gateway;

public sealed record DescriptionUpload(string OperationId, string Epoch, string Kind, long MediaId,
    string? Sha256, GatewayCloudVisionAnalysis? Photo, GatewayVideoSummary? Video);
public sealed record DescriptionUploadReceipt(string OperationId);
public sealed record DescriptionUploadBatch(IReadOnlyList<DescriptionUpload> Items)
{
    public const int MaxItems = 20;
    public const int MaxBytes = 1024 * 1024;
    public const string CapabilityHeader = "X-Picer-Description-Batch";
}
public sealed record DescriptionBatchItemReceipt(string OperationId, string Status);
public sealed record DescriptionBatchReceipt(IReadOnlyList<DescriptionBatchItemReceipt> Items);

public sealed record DescriptionVersion(string VersionId, string Kind, long MediaId, string? Sha256,
    GatewayCloudVisionAnalysis? Photo, GatewayVideoSummary? Video);
