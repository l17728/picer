using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Picer.Core.Identity;
using Picer.Core.Logging;

namespace Picer.IndexService.Identity;

public sealed record IndexRelayConnectorState(bool Enabled, bool Connected, DateTimeOffset? ConnectedAtUtc,
    DateTimeOffset? LastHeartbeatUtc, int ConsecutiveFailures);

/// <summary>Maintains one outbound, authenticated relay control connection for a local IndexService.</summary>
public sealed class IndexRelayConnectorWorker : BackgroundService
{
    private readonly IndexRelayDeviceCredentialStore _credentials;
    private readonly TimeSpan _heartbeatInterval;
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private IndexRelayConnectorState _state;

    public IndexRelayConnectorWorker(IndexRelayDeviceCredentialStore credentials)
        : this(credentials, TimeSpan.FromSeconds(15)) { }

    public IndexRelayConnectorWorker(IndexRelayDeviceCredentialStore credentials, TimeSpan heartbeatInterval)
    {
        if (heartbeatInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));
        _credentials = credentials;
        _heartbeatInterval = heartbeatInterval;
        _state = new(credentials.Exists, false, null, null, 0);
    }

    public IndexRelayConnectorState State { get { lock (_gate) return _state; } }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_credentials.Exists)
        {
            SetState(new(false, false, null, null, 0));
            AppLogger.Info("Index relay connector disabled; no device credential is provisioned");
            return;
        }

        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var client = _credentials.OpenClient();
                using var socket = await client.ConnectWebSocketAsync("api/index/v1/connect", stoppingToken).ConfigureAwait(false);
                failures = 0;
                var connectedAt = DateTimeOffset.UtcNow;
                SetState(new(true, true, connectedAt, null, 0));
                AppLogger.Info("Index relay connector connected; control channel established");
                while (socket.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(_heartbeatInterval, stoppingToken).ConfigureAwait(false);
                    await RelayConnectorProtocol.SendAsync(socket, RelayConnectorProtocol.Heartbeat, stoppingToken).ConfigureAwait(false);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    deadline.CancelAfter(HeartbeatTimeout);
                    var response = await RelayConnectorProtocol.ReceiveAsync(socket, deadline.Token).ConfigureAwait(false);
                    if (response.Type != RelayConnectorProtocol.HeartbeatAck)
                        throw new InvalidDataException("relay_heartbeat_ack_invalid");
                    SetState(new(true, true, connectedAt, DateTimeOffset.UtcNow, 0));
                }
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is HttpRequestException or WebSocketException or IOException or
                                               InvalidDataException or InvalidOperationException or UnauthorizedAccessException or
                                               CryptographicException or FormatException or JsonException or ArgumentException)
            {
                failures = Math.Min(failures + 1, 16);
                SetState(new(true, false, null, null, failures));
                AppLogger.Warn($"Index relay connector disconnected; retry={failures}; error={exception.GetType().Name}");
                var baseSeconds = Math.Min(60, Math.Pow(2, Math.Min(failures - 1, 6)));
                var jitter = Random.Shared.NextDouble() * Math.Min(5, baseSeconds * 0.2);
                try { await Task.Delay(TimeSpan.FromSeconds(baseSeconds + jitter), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
        SetState(new(_credentials.Exists, false, null, null, State.ConsecutiveFailures));
    }

    private void SetState(IndexRelayConnectorState value) { lock (_gate) _state = value; }
}
