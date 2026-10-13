using Picer.Core.Models;
using Picer.Core.Video;

namespace Picer.Core.Gateway;

public sealed record GatewayChangeRequest(string? Epoch = null, long After = 0, long? Through = null, int PageSize = 200, string? Snapshot = null, long? MetadataRevision = null);
public sealed record GatewayMediaChange(long Revision, string Kind, long Id, long SourceId, bool Deleted,
    ImageRecord? Image = null, VideoRecord? Video = null);
public sealed record GatewayChangePage(string Epoch, long Through, long Next, bool HasMore, bool ResetRequired,
    long MetadataRevision, IReadOnlyList<GatewayMediaChange> Items)
{
    public string? Snapshot { get; init; }
    public IReadOnlyList<SourceFolder>? Sources { get; init; }
}
public sealed record MirrorCheckpoint(string Epoch, long MediaRevision, long MetadataRevision);
