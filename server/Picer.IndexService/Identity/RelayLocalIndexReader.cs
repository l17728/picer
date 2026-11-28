using System.Security.Cryptography;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Picer.Core.Identity;

namespace Picer.IndexService.Identity;

internal sealed class RelayLocalIndexReader(IndexApiHandlers handlers)
{
    public async Task ServeAsync(DeviceAccountClient client, RelaySessionInvitation invite, CancellationToken token)
    {
        using var socket = await client.ConnectRelayDataAsync(invite.SessionId, invite.GuiDeviceId, token);
        using var wire = new RelayWireSocket(socket);
        using var offer = client.CreateRelayOffer(invite.SessionId, invite.GuiDeviceId);
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
        handshake.CancelAfter(TimeSpan.FromSeconds(30));
        await wire.SendAsync(RelayWireProtocol.ForKeyExchange(offer.Share), handshake.Token);
        var other = (await wire.ReceiveAsync(handshake.Token)).KeyExchange ?? throw new InvalidDataException("relay_exchange_required");
        using var crypto = await client.AcceptRelayPeerAsync(offer, other, handshake.Token);
        var replay = new RelayReplayWindow();
        await using var originals = new RelayOriginalMediaSession(handlers.LocalMediaGateway);
        await using var queries = new RelayExecutingQueries(token);
        while (true)
        {
            var frame = (await wire.ReceiveAsync(queries.SessionToken)).EncryptedFrame ?? throw new InvalidDataException("relay_request_required");
            var plain = crypto.Decrypt(frame, replay);
            var requestId = Guid.ParseExact(frame.RequestId, "N");
            var operation = Guid.ParseExact(frame.IdempotencyKey, "N");
            try
            {
                if (frame.Kind == "cancel")
                {
                    await queries.CancelAsync(requestId, operation);
                    continue;
                }
                if (frame.Kind != "request") throw new InvalidDataException("relay_request_required");
                var query = DeviceAuthorization.ReadDocument<RelayIndexQuery>(plain, 64 * 1024);
                var asset = RelayIndexQuery.TryAsset(query.Path, out _, out _);
                if (!RelayIndexQuery.IsSupported(query.Method, query.Path) || query.Method == "GET" && !asset && query.Body is not null)
                    throw new InvalidDataException("relay_query_unsupported");
                if (!queries.TryStart(requestId, operation, frame.ExpiresUtc, async requestToken =>
                {
                    var peer = await client.ResolveRelayPeerAsync(invite.GuiDeviceId, requestToken);
                    try
                    {
                        var result = asset || query.Path == RelayIndexQuery.LeasePath
                            ? await originals.HandleAsync(query, peer, requestToken)
                            : await ReadAsync(query, peer, requestToken);
                        try
                        {
                            await RelayResponseFrames.SendAsync(crypto, requestId, operation, result,
                                (part, ct) => wire.SendAsync(RelayWireProtocol.ForEncryptedFrame(part), ct, queries.SessionToken), requestToken);
                        }
                        finally { CryptographicOperations.ZeroMemory(result); }
                    }
                    catch (Exception e) when (e is JsonException or InvalidDataException or ArgumentException or FormatException)
                    {
                        await wire.SendAsync(RelayWireProtocol.ForEncryptedFrame(crypto.Encrypt("error", requestId, operation, [])), queries.SessionToken);
                    }
                }))
                    await wire.SendAsync(RelayWireProtocol.ForEncryptedFrame(crypto.Encrypt("error", requestId, operation, [])), queries.SessionToken);
            }
            catch (Exception e) when (e is JsonException or InvalidDataException or ArgumentException or FormatException)
            {
                await wire.SendAsync(RelayWireProtocol.ForEncryptedFrame(crypto.Encrypt("error", requestId, operation, [])), token);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
    }

    private static T ReadBody<T>(RelayIndexQuery query) where T : class =>
        query.Body?.Deserialize<T>(DeviceAuthorization.Json) ?? throw new InvalidDataException("relay_query_body_invalid");

    private async Task<byte[]> ReadAsync(RelayIndexQuery query, RelayPeerIdentity peer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var db = handlers.OpenSpaceConnection();
        var reader = handlers.ForReadConnection(db);
        if (RelayIndexQuery.TryPhoto(query.Path, out var id, out var preview))
        {
            if (!peer.Scope.Split(' ').Contains(preview ? "media.read" : "thumb.read", StringComparer.Ordinal))
                return RelayPhotoResponse.Status(HttpStatusCode.Forbidden);
            try
            {
                if (!preview)
                {
                    await using var local = await reader.OpenThumbnailAsync(id, token);
                    if (local is not null) return await RelayPhotoResponse.ReadAsync(local, token);
                }
                var image = await reader.GetAsync(id, token);
                if (image is null) return RelayPhotoResponse.Status(HttpStatusCode.NotFound);
                if (reader.AgentPhotoPreview is null) return RelayPhotoResponse.Status(HttpStatusCode.ServiceUnavailable);
                var jpeg = await reader.AgentPhotoPreview(image, null, preview ? 1600 : 256, token);
                using var stream = new MemoryStream(jpeg, writable: false);
                return await RelayPhotoResponse.ReadAsync(stream, token);
            }
            catch (Exception e) when (e is IOException or TimeoutException or WebSocketException or InvalidOperationException)
            { return RelayPhotoResponse.Status(HttpStatusCode.ServiceUnavailable); }
        }
        object value = query.Path switch
        {
            "health" => reader.Health(),
            "api/gateway/v1/sources" => reader.ListSources(),
            "api/gateway/v1/directories" => await reader.ListDirectoriesAsync(token),
            "api/gateway/v1/albums" => reader.ListAlbums(),
            "api/gateway/v1/tags" => reader.ListTags(),
            "api/gateway/v1/browser-metadata" => reader.GetBrowseMetadata(),
            "api/gateway/v1/search" => await reader.SearchAsync(ReadBody<GatewaySearchRequest>(query), token),
            "api/gateway/v1/video-search" => await SearchVideosAsync(ReadBody<GatewayVideoSearchRequest>(query)),
            _ => throw new InvalidDataException("relay_query_unsupported")
        };
        token.ThrowIfCancellationRequested();
        return JsonSerializer.SerializeToUtf8Bytes(value, DeviceAuthorization.Json);

        Task<Picer.Core.Gateway.GatewayVideoPage> SearchVideosAsync(GatewayVideoSearchRequest request) =>
            reader.SearchVideosAsync(request.Query, request.PageSize, request.Cursor, token);
    }
}
