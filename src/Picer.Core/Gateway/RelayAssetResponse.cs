using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Picer.Core.Identity;

namespace Picer.Core.Gateway;

public sealed record RelayAssetRange(string ETag, long Offset, int Count);
public sealed record RelayAssetResponse(int Status, RemoteMediaInfo? Info = null, long Offset = 0)
{
    public byte[] Encode(ReadOnlySpan<byte> body = default)
    {
        var header = JsonSerializer.SerializeToUtf8Bytes(this, DeviceAuthorization.Json);
        var bytes = new byte[4 + header.Length + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(bytes, header.Length);
        header.CopyTo(bytes, 4);
        body.CopyTo(bytes.AsSpan(4 + header.Length));
        return bytes;
    }

    public static HttpResponseMessage Decode(byte[] bytes)
    {
        var size = bytes.Length >= 4 ? BinaryPrimitives.ReadInt32BigEndian(bytes) : 0;
        if (size is <= 0 or > 4096 || size > bytes.Length - 4) throw new InvalidDataException("relay_asset_header_invalid");
        var header = DeviceAuthorization.ReadDocument<RelayAssetResponse>(bytes.AsMemory(4, size), 4096);
        var count = bytes.Length - size - 4;
        var response = new HttpResponseMessage((HttpStatusCode)header.Status) { Content = new ByteArrayContent(bytes, 4 + size, count) };
        if (header.Info is { } info)
        {
            response.Headers.ETag = EntityTagHeaderValue.Parse(info.ETag);
            response.Headers.Add("X-Picer-Media-Streaming", "1");
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(info.ContentType);
            if (header.Status == 206)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(header.Offset, checked(header.Offset + count - 1), info.Length);
            else response.Content.Headers.ContentLength = info.Length;
        }
        return response;
    }
}
