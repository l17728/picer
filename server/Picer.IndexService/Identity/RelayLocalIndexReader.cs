using System.Security.Cryptography;
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
                if (!RelayIndexQuery.IsSupported(query.Method, query.Path) || query.Method == "GET" && query.Body is not null)
                    throw new InvalidDataException("relay_query_unsupported");
                if (!queries.TryStart(requestId, operation, frame.ExpiresUtc, async requestToken =>
                {
                    await client.ResolveRelayPeerAsync(invite.GuiDeviceId, requestToken);
                    try
                    {
                        var result = await ReadAsync(query, requestToken);
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

    private async Task<byte[]> ReadAsync(RelayIndexQuery query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var db = handlers.OpenSpaceConnection();
        var reader = handlers.ForReadConnection(db);
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
