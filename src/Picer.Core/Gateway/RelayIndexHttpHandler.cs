using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Picer.Core.Identity;

namespace Picer.Core.Gateway;

/// <summary>Adapts gateway reads to one authenticated relay data session.</summary>
public sealed class RelayIndexHttpHandler : HttpMessageHandler
{
    private readonly ClientWebSocket _socket;
    private readonly RelayWireSocket _wire;
    private readonly RelaySessionCryptoContext _session;
    private readonly RelayRequestMultiplexer _requests;
    private readonly CancellationTokenSource _closed = new();
    private readonly Task _reader;
    private readonly Uri _baseAddress;
    private readonly object _lifetime = new();
    private readonly TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private bool _disposing;

    private RelayIndexHttpHandler(ClientWebSocket socket, RelayWireSocket wire, RelaySessionCryptoContext session, Uri baseAddress)
    {
        _socket = socket;
        _wire = wire;
        _session = session;
        _baseAddress = baseAddress;
        _requests = new(session, new RelayReplayWindow(), (frame, token) =>
            wire.SendAsync(RelayWireProtocol.ForEncryptedFrame(frame), token, _closed.Token));
        _reader = ReadAsync();
    }

    public static async Task<HttpIndexGateway> ConnectAsync(DeviceAccountClient account, string serviceId, CancellationToken token)
    {
        using var offer = account.CreateRelayOffer(Guid.NewGuid().ToString("N"), serviceId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var socket = await account.ConnectRelayDataAsync(offer.Share.SessionId, serviceId, deadline.Token);
        var wire = new RelayWireSocket(socket);
        try
        {
            await wire.SendAsync(RelayWireProtocol.ForKeyExchange(offer.Share), deadline.Token);
            var remote = (await wire.ReceiveAsync(deadline.Token)).KeyExchange ?? throw new InvalidDataException("relay_exchange_required");
            var session = await account.AcceptRelayPeerAsync(offer, remote, deadline.Token);
            var address = new Uri(DeviceAuthorization.SpaceAddress(account.Connection.Origin, offer.Share.SpaceId) +
                "/relay/" + serviceId + "/" + offer.Share.SenderDeviceId + "/");
            var handler = new RelayIndexHttpHandler(socket, wire, session, address);
            return new HttpIndexGateway(new HttpClient(handler) { BaseAddress = address }, ownsClient: true);
        }
        catch { wire.Dispose(); socket.Dispose(); throw; }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var path = _baseAddress.MakeRelativeUri(request.RequestUri!).ToString();
        if (!RelayIndexQuery.IsSupported(request.Method.Method, path))
            return new(HttpStatusCode.NotImplemented);
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_disposing, this);
            if (_reader.IsCompleted) throw new HttpRequestException("relay_connection_closed");
            _active++;
        }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _closed.Token);
            JsonElement? body = request.Content is null ? null : await request.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: linked.Token).ConfigureAwait(false);
            var query = JsonSerializer.SerializeToUtf8Bytes(new RelayIndexQuery(request.Method.Method, path, body), DeviceAuthorization.Json);
            var response = await _requests.RequestAsync(Guid.NewGuid(), query, linked.Token).ConfigureAwait(false);
            if (RelayIndexQuery.TryPhoto(path, out _, out _)) return RelayPhotoResponse.Decode(response);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(response) };
        }
        finally
        {
            lock (_lifetime)
                if (--_active == 0 && _disposing) _idle.TrySetResult();
        }
    }

    private async Task ReadAsync()
    {
        try
        {
            while (!_closed.IsCancellationRequested)
            {
                var message = await _wire.ReceiveAsync(_closed.Token).ConfigureAwait(false);
                _requests.Accept(message.EncryptedFrame ?? throw new InvalidDataException("relay_response_required"));
            }
        }
        catch (Exception error) { _requests.Fail(error); }
        finally { _requests.Dispose(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_lifetime)
            {
                if (_disposing) return;
                _disposing = true;
                if (_active == 0) _idle.TrySetResult();
            }
            _closed.Cancel();
            _socket.Abort();
            Task.WhenAll(_reader, _idle.Task).GetAwaiter().GetResult();
            _wire.Dispose();
            _session.Dispose();
            _socket.Dispose();
            _closed.Dispose();
        }
        base.Dispose(disposing);
    }
}
