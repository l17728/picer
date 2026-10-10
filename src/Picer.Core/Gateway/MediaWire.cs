using System.Net.WebSockets;
using System.Text.Json;

namespace Picer.Core.Gateway;

/// <summary>One dedicated, authenticated connection per media reader. Pull requests provide backpressure.</summary>
public static class MediaWire
{
    public const int BlockSize = 1024 * 1024;
    public const string Capability = "media.binary.v1";
    public sealed record Metadata(long Length, string ContentType, string ETag, string? Error = null);
    public sealed record ReadRequest(long Offset, int Count);

    public static Task SendJsonAsync<T>(WebSocket socket, T value, CancellationToken token) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value).AsMemory(), WebSocketMessageType.Text, true, token).AsTask();

    public static async Task<byte[]> ReceiveAsync(WebSocket socket, WebSocketMessageType type, int maximum, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        using var result = new MemoryStream();
        var buffer = new byte[Math.Min(maximum + 1, 64 * 1024)];
        while (true)
        {
            var part = await socket.ReceiveAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false);
            if (part.MessageType != type) throw new IOException("media_connection_closed_or_invalid");
            if (result.Length + part.Count > maximum) throw new InvalidDataException("media_message_too_large");
            result.Write(buffer, 0, part.Count);
            if (part.EndOfMessage) return result.ToArray();
        }
    }

    public static async Task<T> ReceiveJsonAsync<T>(WebSocket socket, CancellationToken token) =>
        JsonSerializer.Deserialize<T>(await ReceiveAsync(socket, WebSocketMessageType.Text, 8192, token).ConfigureAwait(false))
        ?? throw new InvalidDataException("media_metadata_invalid");
}

/// <summary>Seekable remote reader, bounded to one binary block. Never downloads an entire file in memory.</summary>
public sealed class SocketMediaStream(WebSocket socket, MediaWire.Metadata metadata, Action onDispose) : Stream
{
    private byte[] _block = [];
    private long _blockOffset;
    private long _position;
    private int _disposed;
    public MediaWire.Metadata Metadata { get; } = metadata;
    public override bool CanRead => _disposed == 0;
    public override bool CanSeek => _disposed == 0;
    public override bool CanWrite => false;
    public override long Length => Metadata.Length;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var next = checked(offset + (origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? _position : Length));
        if (next < 0 || next > Length) throw new IOException("media_seek_out_of_range");
        return _position = next;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (buffer.IsEmpty || _position == Length) return 0;
        if (_block.Length == 0 || _position < _blockOffset || _position >= _blockOffset + _block.Length)
        {
            var remaining = Length - _position;
            if (_position < _readAheadEnd) remaining = Math.Min(remaining, _readAheadEnd - _position);
            var count = (int)Math.Min(MediaWire.BlockSize, remaining);
            try
            {
                await MediaWire.SendJsonAsync(socket, new MediaWire.ReadRequest(_position, count), cancellationToken).ConfigureAwait(false);
                _block = await MediaWire.ReceiveAsync(socket, WebSocketMessageType.Binary, count, cancellationToken).ConfigureAwait(false);
                if (_block.Length != count) throw new EndOfStreamException("media_source_changed");
                _blockOffset = _position;
            }
            catch { Dispose(); throw; }
        }
        var start = checked((int)(_position - _blockOffset));
        var copied = Math.Min(buffer.Length, _block.Length - start);
        _block.AsMemory(start, copied).CopyTo(buffer);
        _position += copied;
        return copied;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    private long _readAheadEnd;
    // Preserve the full seekable representation while avoiding reads past an HTTP range.
    // A full-response fallback (for example If-Range mismatch) can still read beyond it.
    public void LimitReadAhead(long endExclusive)
    {
        if (endExclusive <= 0 || endExclusive > Length) throw new ArgumentOutOfRangeException(nameof(endExclusive));
        _readAheadEnd = endExclusive;
    }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) { socket.Abort(); socket.Dispose(); _block = []; onDispose(); }
        base.Dispose(disposing);
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
