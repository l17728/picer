using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;

namespace Picer.Core.Identity;

public static class RelayPhotoResponse
{
    private const int HeaderSize = sizeof(ushort);

    public static byte[] Status(HttpStatusCode status)
    {
        var bytes = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)status);
        return bytes;
    }

    public static async Task<byte[]> ReadAsync(Stream? image, CancellationToken token)
    {
        if (image is null) return Status(HttpStatusCode.NotFound);
        var length = image.Length - image.Position;
        if (length <= 0 || length > RelayResponseFrames.MaximumResponseBytes - HeaderSize)
            return Status(HttpStatusCode.RequestEntityTooLarge);
        var bytes = new byte[HeaderSize + (int)length];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)HttpStatusCode.OK);
        await image.ReadExactlyAsync(bytes.AsMemory(HeaderSize), token).ConfigureAwait(false);
        return bytes;
    }

    public static HttpResponseMessage Decode(byte[] bytes)
    {
        if (bytes.Length < HeaderSize) throw new InvalidDataException("relay_photo_response_invalid");
        var status = (HttpStatusCode)BinaryPrimitives.ReadUInt16BigEndian(bytes);
        if (status == HttpStatusCode.OK && bytes.Length > HeaderSize)
        {
            var body = new ByteArrayContent(bytes, HeaderSize, bytes.Length - HeaderSize);
            body.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return new(status) { Content = body };
        }
        if (bytes.Length == HeaderSize && status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.RequestEntityTooLarge) return new(status);
        throw new InvalidDataException("relay_photo_response_invalid");
    }
}
