using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Picer.Core.Identity;

public static class RelayResponseFrames
{
    internal const int HeaderBytes = 8;
    internal const int ChunkBytes = RelaySessionCryptoContext.MaximumPayloadBytes - HeaderBytes;
    public const int MaximumResponseBytes = 8 * 1024 * 1024;

    public static async Task SendAsync(RelaySessionCryptoContext session, Guid requestId, Guid idempotencyKey,
        ReadOnlyMemory<byte> response, Func<RelayEncryptedFrame, CancellationToken, Task> send, CancellationToken token)
    {
        if (response.Length > MaximumResponseBytes) throw new InvalidDataException("relay_response_too_large");
        token.ThrowIfCancellationRequested();
        if (response.Length <= RelaySessionCryptoContext.MaximumPayloadBytes)
        {
            await send(session.Encrypt("response", requestId, idempotencyKey, response.Span), token).ConfigureAwait(false);
            return;
        }
        for (var offset = 0; offset < response.Length; offset += ChunkBytes)
        {
            token.ThrowIfCancellationRequested();
            var length = Math.Min(ChunkBytes, response.Length - offset);
            var chunk = new byte[HeaderBytes + length];
            RelayEncryptedFrame frame;
            try
            {
                BinaryPrimitives.WriteInt32BigEndian(chunk, response.Length);
                BinaryPrimitives.WriteInt32BigEndian(chunk.AsSpan(4), offset);
                response.Slice(offset, length).CopyTo(chunk.AsMemory(HeaderBytes));
                frame = session.Encrypt("chunk", requestId, idempotencyKey, chunk);
            }
            finally { CryptographicOperations.ZeroMemory(chunk); }
            await send(frame, token).ConfigureAwait(false);
        }
    }
}
