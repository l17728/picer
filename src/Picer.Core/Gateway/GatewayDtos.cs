using Picer.Core.Models;

namespace Picer.Core.Gateway;

public sealed record GatewaySourceRegistration(string Path);

public sealed record GatewayScanStart(long SourceId);

public sealed record GatewayScanStarted(string JobId);

public sealed record ScanJobInfo(string JobId, long SourceId, string Stage, int Processed, int Total, bool Done, string? Error);

public sealed record GatewayVerifiedChangesRequest(IReadOnlyList<long> ImageIds);

public sealed record GatewayVerifiedChangesResult(int Removed);
