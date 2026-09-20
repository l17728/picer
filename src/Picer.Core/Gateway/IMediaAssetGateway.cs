namespace Picer.Core.Gateway;

/// <summary>Opens original media through the local filesystem or the remote media agent.</summary>
public interface IMediaAssetGateway
{
    Task<Stream?> OpenOriginalImageAsync(long imageId, string? agentId = null, CancellationToken cancellationToken = default);
    Task<Stream?> OpenOriginalVideoAsync(long videoId, string? agentId = null, CancellationToken cancellationToken = default);
}
