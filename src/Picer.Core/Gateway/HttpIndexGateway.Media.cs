using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Picer.Core.Gateway;

public sealed record RemoteMediaInfo(long Length, string ETag, string ContentType, bool SupportsStreaming);
public sealed record MediaLeaseRequest(string ETag, bool Release);
public interface IRangeMediaGateway
{
    Task<RemoteMediaInfo> GetMediaInfoAsync(long id, bool video, CancellationToken token = default);
    Task<Stream> OpenMediaRangeAsync(long id, bool video, RemoteMediaInfo expected, long offset, int count, CancellationToken token = default);
    Task ManageMediaLeaseAsync(string etag, bool release, CancellationToken token = default) => Task.CompletedTask;
}

public sealed partial class HttpIndexGateway
{
    public async Task<Stream?> OpenPhotoAnalysisInputAsync(Picer.Core.Models.ImageRecord image, CancellationToken cancellationToken = default)
    {
        var identity = new MediaWire.SourceFingerprint(image.FileSize, image.ModifiedUtc, image.ContentSha256 ?? image.Sha256);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/gateway/v1/analysis-input")
        { Content = JsonContent.Create(new MediaWire.AnalysisInputRequest(image.Id, identity)) };
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented or HttpStatusCode.MethodNotAllowed)
        { response.Dispose(); return null; }
        try
        {
            if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
                throw new InvalidDataException("照片内容或索引已变化，请先更新索引。");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType != "image/jpeg"
                || response.Content.Headers.ContentLength is not (> 0 and <= 8 * 1024 * 1024)
                || !response.Headers.TryGetValues("X-Picer-Analysis-Input", out var values) || !values.Contains("verified-v1"))
                throw new InvalidDataException("photo_analysis_input_invalid");
            return await OwnResponseAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch { response.Dispose(); throw; }
    }

    private Task<HttpResponseMessage> GetMediaWithRetryAsync(string route, CancellationToken token,
        [System.Runtime.CompilerServices.CallerMemberName] string operation = "") =>
        SendWithRetryAsync(ct => _client.GetAsync(route, HttpCompletionOption.ResponseHeadersRead, ct), token, operation);
    public async Task<Stream?> OpenPreviewAsync(long id, CancellationToken token = default)
    {
        var response = await _client.GetAsync($"api/gateway/v1/previews/{id}", HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) { response.Dispose(); return null; }
        return await OwnResponseAsync(response, token).ConfigureAwait(false);
    }
    public async Task ManageMediaLeaseAsync(string etag, bool release, CancellationToken token = default)
    {
        using var response = await _client.PostAsJsonAsync("api/gateway/v1/media-lease", new MediaLeaseRequest(etag, release), token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
    public string MediaCacheScope => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_client.BaseAddress!.AbsoluteUri)));
    public async Task<RemoteMediaInfo> GetMediaInfoAsync(long id, bool video, CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, $"api/gateway/v1/assets/{(video ? "videos" : "images")}/{id}");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound)
            return new(0, "", "application/octet-stream", false);
        response.EnsureSuccessStatusCode();
        var etag = response.Headers.ETag;
        var length = response.Content.Headers.ContentLength ?? -1;
        return new(length, etag?.ToString() ?? "", response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream",
            length >= 0 && etag is { IsWeak: false } && response.Headers.TryGetValues("X-Picer-Media-Streaming", out var values) && values.Contains("1"));
    }
    public async Task<Stream> OpenMediaRangeAsync(long id, bool video, RemoteMediaInfo expected, long offset, int count, CancellationToken token = default)
    {
        if (!expected.SupportsStreaming || offset < 0 || count <= 0 || offset > expected.Length - count) throw new ArgumentOutOfRangeException(nameof(offset));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/gateway/v1/assets/{(video ? "videos" : "images")}/{id}");
        request.Headers.Range = new RangeHeaderValue(offset, offset + count - 1);
        request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(expected.ETag));
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        try
        {
            if (response.StatusCode == HttpStatusCode.PreconditionFailed) throw new InvalidDataException("源文件已变化，请重新打开。");
            response.EnsureSuccessStatusCode();
            var range = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != offset || range.To != offset + count - 1
                || range.Length != expected.Length || response.Content.Headers.ContentLength != count || response.Headers.ETag?.ToString() != expected.ETag)
                throw new InvalidDataException("远程分块响应与请求身份不一致。");
            return await OwnResponseAsync(response, token).ConfigureAwait(false);
        }
        catch { response.Dispose(); throw; }
    }
    private static async Task<Stream> OwnResponseAsync(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            await EnsureSuccessAsync(response, token).ConfigureAwait(false);
            return new ResponseOwnedStream(await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false), response);
        }
        catch { response.Dispose(); throw; }
    }
}

internal sealed class ResponseOwnedStream(Stream source, HttpResponseMessage response) : Stream
{
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        return await source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
    protected override void Dispose(bool disposing) { if (disposing) { source.Dispose(); response.Dispose(); } base.Dispose(disposing); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
