using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Picer.Core.Gateway;
using Picer.Core.Identity;

namespace Picer.IndexService.Identity;

internal sealed class RelayOriginalMediaSession(IRangeMediaGateway? gateway) : IAsyncDisposable
{
    private sealed record Lease(long Id, bool Video, RemoteMediaInfo Info);
    private readonly ConcurrentDictionary<string, Lease> _leases = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots = new(32);

    public async Task<byte[]> HandleAsync(RelayIndexQuery query, RelayPeerIdentity peer, CancellationToken token)
    {
        if (!peer.Scope.Split(' ').Contains("media.read", StringComparer.Ordinal)) return Status(403);
        if (gateway is null) return Status(503);
        try
        {
            if (query.Path == RelayIndexQuery.LeasePath)
            {
                var change = query.Body?.Deserialize<MediaLeaseRequest>(DeviceAuthorization.Json);
                if (change is null || string.IsNullOrEmpty(change.ETag) || !_leases.ContainsKey(change.ETag)) return Status(412);
                await gateway.ManageMediaLeaseAsync(change.ETag, change.Release, token);
                if (change.Release) Forget(change.ETag);
                return Status(204);
            }
            if (!RelayIndexQuery.TryAsset(query.Path, out var id, out var video)) return Status(404);
            if (query.Method == "HEAD")
            {
                if (!await _slots.WaitAsync(0, token)) return Status(429);
                var registered = false;
                try
                {
                    var info = await gateway.GetMediaInfoAsync(id, video, token);
                    if (!info.SupportsStreaming) return Status(405);
                    registered = _leases.TryAdd(info.ETag, new(id, video, info));
                    if (!registered) return Status(409);
                    return new RelayAssetResponse(200, info).Encode();
                }
                finally { if (!registered) _slots.Release(); }
            }
            var range = query.Body?.Deserialize<RelayAssetRange>(DeviceAuthorization.Json);
            if (range is null || string.IsNullOrEmpty(range.ETag) || !_leases.TryGetValue(range.ETag, out var lease) ||
                lease.Id != id || lease.Video != video) return Status(412);
            if (range.Offset < 0 || range.Count is <= 0 or > RelayResponseFrames.MaximumResponseBytes - 4096 ||
                range.Offset > lease.Info.Length - range.Count) return Status(416);
            try
            {
                await using var source = await gateway.OpenMediaRangeAsync(id, video, lease.Info, range.Offset, range.Count, token);
                var bytes = new byte[range.Count];
                try
                {
                    await source.ReadExactlyAsync(bytes, token);
                    return new RelayAssetResponse(206, lease.Info, range.Offset).Encode(bytes);
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            catch (InvalidDataException) { return Status(412); }
        }
        catch (HttpRequestException e) { return Status((int)(e.StatusCode ?? HttpStatusCode.ServiceUnavailable)); }
        catch (IOException) { return Status(503); }
    }

    private static byte[] Status(int code) => new RelayAssetResponse(code).Encode();
    private void Forget(string etag)
    {
        if (_leases.TryRemove(etag, out _)) _slots.Release();
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            if (gateway is not null)
                await Task.WhenAll(_leases.Keys.Select(tag => gateway.ManageMediaLeaseAsync(tag, true, timeout.Token)));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { _leases.Clear(); _slots.Dispose(); }
    }
}
