namespace Picer.Core.Gateway;

public sealed record DescriptionUpload(string OperationId, string Epoch, string Kind, long MediaId,
    string? Sha256, GatewayCloudVisionAnalysis? Photo, GatewayVideoSummary? Video);
public sealed record DescriptionUploadReceipt(string OperationId);

public sealed record DescriptionVersion(string VersionId, string Kind, long MediaId, string? Sha256,
    GatewayCloudVisionAnalysis? Photo, GatewayVideoSummary? Video);
