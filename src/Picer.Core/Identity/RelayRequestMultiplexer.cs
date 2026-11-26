using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;

namespace Picer.Core.Identity;

/// <summary>Correlates bounded concurrent encrypted requests and responses over a shared relay session.</summary>
public sealed class RelayRequestMultiplexer : IDisposable
{
    private sealed record Pending(Guid IdempotencyKey, TaskCompletionSource<byte[]> Completion);
    private readonly RelaySessionCryptoContext _crypto;
    private readonly RelayReplayWindow _replayWindow;
    private readonly Func<RelayEncryptedFrame, CancellationToken, Task> _send;
    private readonly ConcurrentDictionary<Guid, Pending> _pending = new();
    private readonly SemaphoreSlim _capacity;
    private readonly TimeSpan _defaultTimeout;
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    public RelayRequestMultiplexer(RelaySessionCryptoContext crypto, RelayReplayWindow replayWindow,
        Func<RelayEncryptedFrame, CancellationToken, Task> send, int maxInFlight = 32,
        TimeSpan? defaultTimeout = null)
    {
        if (maxInFlight is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maxInFlight));
        _crypto = crypto;
        _replayWindow = replayWindow;
        _send = send;
        _capacity = new SemaphoreSlim(maxInFlight, maxInFlight);
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromMinutes(2);
        if (_defaultTimeout <= TimeSpan.Zero || _defaultTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(defaultTimeout));
    }

    public int PendingCount => _pending.Count;

    public async Task<byte[]> RequestAsync(Guid idempotencyKey, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (idempotencyKey == Guid.Empty) throw new ArgumentException("relay_idempotency_key_required", nameof(idempotencyKey));
        var effectiveTimeout = timeout ?? _defaultTimeout;
        if (effectiveTimeout <= TimeSpan.Zero || effectiveTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        await _capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (Volatile.Read(ref _disposed) != 0)
        {
            _capacity.Release();
            throw new ObjectDisposedException(nameof(RelayRequestMultiplexer));
        }
        var requestId = Guid.NewGuid();
        var pending = new Pending(idempotencyKey, new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_pending.TryAdd(requestId, pending))
        {
            _capacity.Release();
            throw new InvalidOperationException("relay_request_id_collision");
        }

        var sent = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        deadline.CancelAfter(effectiveTimeout);
        try
        {
            var request = _crypto.Encrypt("request", requestId, idempotencyKey, payload.Span, lifetime: effectiveTimeout);
            sent = true;
            await _send(request, deadline.Token).ConfigureAwait(false);
            return await pending.Completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(RelayRequestMultiplexer));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (sent && !pending.Completion.Task.IsCompleted) await SendCancelBestEffortAsync(requestId, idempotencyKey).ConfigureAwait(false);
            throw new TimeoutException("relay_request_timed_out");
        }
        catch (OperationCanceledException)
        {
            if (sent && !pending.Completion.Task.IsCompleted) await SendCancelBestEffortAsync(requestId, idempotencyKey).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
            _capacity.Release();
        }
    }

    /// <summary>Called by the single WebSocket reader after decoding one authenticated wire frame.</summary>
    public void Accept(RelayEncryptedFrame frame)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var plaintext = _crypto.Decrypt(frame, _replayWindow);
        try
        {
            if (frame.Kind is not ("response" or "error") || !Guid.TryParseExact(frame.RequestId, "N", out var requestId))
                throw new InvalidDataException("relay_response_frame_invalid");
            if (!_pending.TryGetValue(requestId, out var pending)) return;
            if (!Guid.TryParseExact(frame.IdempotencyKey, "N", out var idempotencyKey) || idempotencyKey != pending.IdempotencyKey)
                throw new InvalidDataException("relay_response_idempotency_mismatch");
            if (frame.Kind == "error")
                pending.Completion.TrySetException(new IOException("relay_remote_request_failed"));
            else if (!pending.Completion.TrySetResult(plaintext))
                return;
            else
                plaintext = [];
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public void Fail(Exception reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        foreach (var pending in _pending.Values) pending.Completion.TrySetException(reason);
    }

    private async Task SendCancelBestEffortAsync(Guid requestId, Guid idempotencyKey)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var cancel = _crypto.Encrypt("cancel", requestId, idempotencyKey, ReadOnlySpan<byte>.Empty);
            await _send(cancel, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or WebSocketException or ObjectDisposedException)
        {
            // Cancellation is best effort. The request still expires at the receiver.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        Fail(new ObjectDisposedException(nameof(RelayRequestMultiplexer)));
    }
}
