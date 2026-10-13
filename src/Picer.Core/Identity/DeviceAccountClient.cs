using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Net.WebSockets;

namespace Picer.Core.Identity;

public sealed record DeviceConnection(string Origin, string ServiceId, string SpkiPin, string SigningKey,
    string KeyName, string RequestId, string DeviceName, string ClientKind, DeviceSession? Session = null, string? RefreshAttempt = null);
public sealed record ServiceDiscovery(int Version, string ServiceId, string Origin, string SpkiPin, string SigningKey,
    string EnrollmentEndpoint, string AccountEndpoint);
public sealed record EnrollmentStarted(string RequestId, string ApprovalUrl, int ExpiresIn);

/// <summary>One client per device grant. Refresh state is persisted before transmission so interrupted retries keep their attempt ID.</summary>
public sealed class DeviceAccountClient : IDisposable
{
    private DeviceConnection _connection;
    private readonly ECDsa _key;
    private readonly object _keyGate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Action<DeviceConnection> _persist;
    private readonly HttpClient _identity;
    private readonly HttpMessageHandler _transport;
    private readonly HttpClient _space;
    private readonly Func<DeviceConnection>? _reload;
    private readonly Func<CancellationToken, Task<IDisposable>>? _acquireRefreshLease;
    public DeviceConnection Connection => Volatile.Read(ref _connection);
    public HttpClient SpaceClient => _space;

    public async Task<ClientWebSocket> ConnectWebSocketAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        if (relativePath != "api/agent/v1/connect" &&
            !(relativePath.StartsWith("api/agent/v1/media/", StringComparison.Ordinal) &&
              relativePath[19..] is { Length: 64 } ticket && ticket.All(Uri.IsHexDigit)))
            throw new ArgumentException("invalid_agent_socket_path", nameof(relativePath));
        var session = await SessionAsync(cancellationToken);
        if (Connection.ClientKind != "agent" || !session.Scope.Split(' ').Contains("source.serve"))
            throw new UnauthorizedAccessException("agent_authorization_required");
        var endpoint = new Uri(DeviceAuthorization.SpaceAddress(Connection.Origin, session.SpaceId) + "/" + relativePath);
        var socket = new ClientWebSocket();
        var pin = Convert.FromBase64String(Connection.SpkiPin);
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
        {
            if (certificate is null) return false;
            using var cert = new X509Certificate2(certificate);
            return CryptographicOperations.FixedTimeEquals(cert.PublicKey.ExportSubjectPublicKeyInfo(), pin);
        };
        socket.Options.SetRequestHeader("Authorization", "DPoP " + session.AccessToken);
        socket.Options.SetRequestHeader("DPoP", Sign("GET", endpoint, session.AccessToken));
        try
        {
            await socket.ConnectAsync(new UriBuilder(endpoint) { Scheme = "wss" }.Uri, cancellationToken);
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    public HttpClient CreateSpaceClient(TimeSpan? timeout = null)
    {
        var current = Connection;
        var session = current.Session ?? throw new InvalidOperationException("device_login_required");
        return new HttpClient(new AuthorizationHandler(this, _transport))
        {
            BaseAddress = new Uri(DeviceAuthorization.SpaceAddress(current.Origin, session.SpaceId) + "/"),
            Timeout = timeout ?? TimeSpan.FromMinutes(2)
        };
    }

    public DeviceAccountClient(DeviceConnection connection, ECDsa key, Action<DeviceConnection> persist,
        Func<DeviceConnection>? reload = null, Func<CancellationToken, Task<IDisposable>>? acquireRefreshLease = null)
        : this(connection, key, persist, CreateTransport(connection.SpkiPin), reload, acquireRefreshLease) { }

    public DeviceAccountClient(DeviceConnection connection, ECDsa key, Action<DeviceConnection> persist, HttpMessageHandler transport,
        Func<DeviceConnection>? reload = null, Func<CancellationToken, Task<IDisposable>>? acquireRefreshLease = null)
    {
        _connection = connection with { Origin = DeviceAuthorization.NormalizeOrigin(connection.Origin) };
        _key = key; _persist = persist; _transport = transport;
        _reload = reload; _acquireRefreshLease = acquireRefreshLease;
        _identity = new HttpClient(transport, disposeHandler: false) { BaseAddress = new Uri(_connection.Origin), Timeout = TimeSpan.FromSeconds(30) };
        _space = new HttpClient(new AuthorizationHandler(this, transport), disposeHandler: true) { Timeout = TimeSpan.FromMinutes(2) };
        if (connection.Session is { } session) _space.BaseAddress = new Uri(DeviceAuthorization.SpaceAddress(connection.Origin, session.SpaceId) + "/");
    }

    public static HttpMessageHandler CreateTransport(string spkiPin)
    {
        var pin = Convert.FromBase64String(spkiPin);
        if (pin.Length is < 64 or > 2048) throw new ArgumentException("invalid_service_pin");
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, MaxConnectionsPerServer = 12,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate | System.Net.DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
        {
            if (certificate is null) return false;
            using var cert = new X509Certificate2(certificate);
            return CryptographicOperations.FixedTimeEquals(cert.PublicKey.ExportSubjectPublicKeyInfo(), pin);
        };
        return handler;
    }

    public async Task<EnrollmentStarted> BeginEnrollmentAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            using var processLease = _acquireRefreshLease is null ? null : await _acquireRefreshLease(cancellationToken);
            var current = Reload();
            if (current.Session is not null) throw new InvalidOperationException("device_already_paired");
            var request = new DeviceEnrollmentRequest(current.RequestId, DeviceAuthorization.ExportPublicKey(_key), current.DeviceName, current.ClientKind);
            try { return await SendIdentityAsync<EnrollmentStarted>("/identity/enrollments", request, null, cancellationToken); }
            catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                Save(current with { RequestId = Guid.NewGuid().ToString("N"), RefreshAttempt = null });
                return await SendIdentityAsync<EnrollmentStarted>("/identity/enrollments", request with { RequestId = Connection.RequestId }, null, cancellationToken);
            }
        }
        finally { _refreshGate.Release(); }
    }

    public async Task ImportAsync(ReadOnlyMemory<byte> document, CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            using var processLease = _acquireRefreshLease is null ? null : await _acquireRefreshLease(cancellationToken);
            var current = Reload();
            if (current.Session is not null) throw new InvalidOperationException("device_already_paired");
            var file = DeviceAuthorization.ReadFileDocument(document);
            using var signingKey = DeviceAuthorization.ReadPublicKey(current.SigningKey);
            DeviceAuthorization.ValidateFile(file, current.Origin, current.ServiceId, current.RequestId,
                DeviceAuthorization.ExportPublicKey(_key), signingKey, DateTimeOffset.UtcNow);
            if (file.SpkiPin != current.SpkiPin) throw new InvalidDataException("service_trust_changed");
            var attempt = current.RefreshAttempt ?? Guid.NewGuid().ToString("N");
            Save(current with { RefreshAttempt = attempt });
            var session = await SendIdentityAsync<DeviceSession>("/identity/redeem", new
            { requestId = file.RequestId, redemptionCode = file.RedemptionCode, attemptId = attempt }, null, cancellationToken);
            ValidateSession(session, current, file.SpaceId);
            Save(current with { Session = session, RefreshAttempt = null });
            _space.BaseAddress = new Uri(DeviceAuthorization.SpaceAddress(current.Origin, session.SpaceId) + "/");
            await SendIdentityAsync<JsonElement>("/identity/activate", new { }, session.AccessToken, cancellationToken);
        }
        finally { _refreshGate.Release(); }
    }

    private void Save(DeviceConnection value)
    {
        _persist(value); // Publication follows durable save; failure must not discard the old refresh family.
        Volatile.Write(ref _connection, value);
    }

    private DeviceConnection Reload()
    {
        if (_reload is null) return Connection;
        var value = _reload();
        var current = Connection;
        if (value.Origin != current.Origin || value.ServiceId != current.ServiceId || value.KeyName != current.KeyName || value.RequestId != current.RequestId)
            throw new InvalidOperationException("device_connection_replaced");
        Volatile.Write(ref _connection, value);
        return value;
    }

    private static void ValidateSession(DeviceSession session, DeviceConnection connection, string space)
    {
        if (session.ServiceId != connection.ServiceId || session.SpaceId != space ||
            !Guid.TryParseExact(session.GrantId, "N", out _) || !Guid.TryParseExact(session.DeviceId, "N", out _) ||
            DeviceAuthorization.DecodeBase64Url(session.AccessToken).Length != 32 || DeviceAuthorization.DecodeBase64Url(session.RefreshToken).Length != 32)
            throw new InvalidDataException("invalid_device_session");
    }

    private async Task<DeviceSession> SessionAsync(CancellationToken cancellationToken)
    {
        var current = Connection;
        var session = current.Session ?? throw new InvalidOperationException("device_login_required");
        if (session.ExpiresUtc > DateTimeOffset.UtcNow.AddSeconds(30) && current.RefreshAttempt is null) return session;
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            using var processLease = _acquireRefreshLease is null ? null : await _acquireRefreshLease(cancellationToken);
            current = Reload();
            session = current.Session ?? throw new InvalidOperationException("device_login_required");
            if (session.ExpiresUtc > DateTimeOffset.UtcNow.AddSeconds(30) && current.RefreshAttempt is null) return session;
            var attempt = current.RefreshAttempt ?? Guid.NewGuid().ToString("N");
            Save(current with { RefreshAttempt = attempt });
            var refreshed = await SendIdentityAsync<DeviceSession>("/identity/refresh", new { refreshToken = session.RefreshToken, attemptId = attempt }, null, cancellationToken);
            ValidateSession(refreshed, current, session.SpaceId);
            Save(current with { Session = refreshed, RefreshAttempt = null });
            return refreshed;
        }
        finally { _refreshGate.Release(); }
    }

    private string Sign(string method, Uri uri, string? token)
    {
        lock (_keyGate) return DeviceProof.Create(_key, method, uri, DateTimeOffset.UtcNow, token);
    }

    private async Task<T> SendIdentityAsync<T>(string path, object body, string? access, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(Connection.Origin + path))
        { Content = JsonContent.Create(body, options: DeviceAuthorization.Json) };
        message.Headers.Add("DPoP", Sign("POST", message.RequestUri!, access));
        if (access is not null) message.Headers.Authorization = new AuthenticationHeaderValue("DPoP", access);
        using var response = await _identity.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return DeviceAuthorization.ReadDocument<T>(bytes);
    }

    private sealed class BorrowedTransport(HttpMessageHandler transport) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _invoker = new(transport, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _invoker.SendAsync(request, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing) _invoker.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class AuthorizationHandler(DeviceAccountClient owner, HttpMessageHandler transport) : DelegatingHandler(new BorrowedTransport(transport))
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var current = owner.Connection;
            var uri = request.RequestUri ?? throw new InvalidOperationException("missing_request_uri");
            var expected = DeviceAuthorization.SpaceAddress(current.Origin, current.Session?.SpaceId ?? throw new InvalidOperationException("device_login_required")) + "/";
            if (!uri.AbsoluteUri.StartsWith(expected, StringComparison.Ordinal)) throw new UnauthorizedAccessException("credential_target_mismatch");
            var session = await owner.SessionAsync(cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", session.AccessToken);
            request.Headers.Remove("DPoP");
            request.Headers.Add("DPoP", owner.Sign(request.Method.Method, uri, session.AccessToken));
            return await base.SendAsync(request, cancellationToken);
        }
    }

    public void Dispose()
    {
        _space.Dispose(); _identity.Dispose(); _transport.Dispose(); _key.Dispose(); _refreshGate.Dispose();
    }
}
