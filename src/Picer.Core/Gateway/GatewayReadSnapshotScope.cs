namespace Picer.Core.Gateway;

/// <summary>Release the remote read cut after publication or cancellation; server expiry remains a crash fallback.</summary>
public sealed class GatewayReadSnapshotScope(IIndexGateway gateway) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await gateway.ReleaseReadSnapshotAsync(deadline.Token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException) { }
    }
}
