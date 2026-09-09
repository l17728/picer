using System.Buffers.Binary;
using System.IO.Pipes;

namespace Picer.Core.Gateway;

public static class PipeFrames
{
    public const int MaxFrameBytes = 32 * 1024 * 1024;

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 0 or > MaxFrameBytes) throw new InvalidDataException($"Invalid pipe frame length: {length}");
        var payload = new byte[length];
        await ReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    public static async Task<NamedPipeClientStream> ConnectNamedPipeAsync(string pipeName, CancellationToken cancellationToken = default)
    {
        var attempts = 0;
        while (true)
        {
            var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await stream.ConnectAsync(3000, cancellationToken).ConfigureAwait(false);
                return stream;
            }
            catch (TimeoutException) when (++attempts < 3)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Pipe closed by peer.");
            read += count;
        }
    }
}
