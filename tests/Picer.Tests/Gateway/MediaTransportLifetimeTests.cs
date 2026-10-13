using System.Net.WebSockets;
using Picer.Core.Gateway;

namespace Picer.Tests;

public sealed class MediaTransportLifetimeTests
{
    [Fact]
    public async Task StalledSendTimesOutAndReturnsCapacityExactlyOnce()
    {
        var returned = 0;
        using var socket = new StalledSocket();
        using var stream = new SocketMediaStream(socket, new(1, "image/jpeg", "\"test\""),
            () => Interlocked.Increment(ref returned), ioTimeout: TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        stream.Dispose();
        Assert.Equal(1, returned);
        Assert.False(stream.CanRead);
    }

    [Fact]
    public void DisconnectedSourceReturnsCapacityEvenWhenSocketAbortThrows()
    {
        var returned = 0;
        using var socket = new StalledSocket { ThrowOnAbort = true };
        using var disconnected = new CancellationTokenSource();
        using var stream = new SocketMediaStream(socket, new(1, "image/jpeg", "\"test\""), () => returned++);
        stream.CancelWhen(disconnected.Token);
        disconnected.Cancel();
        stream.Dispose();
        Assert.Equal(1, returned);
        Assert.False(stream.CanRead);
    }

    private sealed class StalledSocket : WebSocket
    {
        public bool ThrowOnAbort;
        private WebSocketState _state = WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override WebSocketState State => _state;
        public override void Abort() { _state = WebSocketState.Aborted; if (ThrowOnAbort) throw new ObjectDisposedException(nameof(StalledSocket)); }
        public override void Dispose() => _state = WebSocketState.Closed;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); throw new InvalidOperationException(); }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
