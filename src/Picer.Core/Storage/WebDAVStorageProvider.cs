using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace Picer.Core.Storage;

public sealed class WebDAVStorageProvider : IStorageProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _serverUrl;
    private readonly string _basePath;

    public WebDAVStorageProvider(string serverUrl, string username, string password, string basePath = "/")
        : this(serverUrl, username, password, basePath, new HttpClientHandler())
    {
    }

    public WebDAVStorageProvider(string serverUrl, string username, string password, string basePath, HttpMessageHandler handler)
    {
        _serverUrl = serverUrl.TrimEnd('/');
        _basePath = basePath.StartsWith('/') ? basePath : "/" + basePath;

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };

        if (!string.IsNullOrEmpty(username))
        {
            var bytes = Encoding.ASCII.GetBytes($"{username}:{password}");
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));
        }
    }

    public string Scheme => "webdav";

    public IEnumerable<StorageFileInfo> EnumerateFiles(string root)
    {
        return EnumerateFilesAsync(root).GetAwaiter().GetResult();
    }

    public async Task<IEnumerable<StorageFileInfo>> EnumerateFilesAsync(string root, CancellationToken ct = default)
    {
        var results = new List<StorageFileInfo>();
        var pending = new Queue<string>();
        pending.Enqueue(BuildUrl(root));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var batch = new List<string>(4);
            while (batch.Count < 4 && pending.Count > 0) batch.Add(pending.Dequeue());
            var listings = await Task.WhenAll(batch.Select(url => PropfindAsync(url, ct))).ConfigureAwait(false);
            for (var i = 0; i < batch.Count; i++)
            {
                foreach (var item in listings[i])
                {
                    if (item.IsCollection)
                    {
                        if (!UrlsReferToSameResource(item.Href, batch[i])) pending.Enqueue(item.Href);
                    }
                    else
                    {
                        results.Add(new StorageFileInfo
                        {
                            Key = item.Href, Size = item.ContentLength, ModifiedUtc = item.LastModified, ETag = item.ETag
                        });
                    }
                }
            }
        }

        return results;
    }

    public bool Exists(string key)
    {
        return ExistsAsync(key).GetAwaiter().GetResult();
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        var url = BuildUrl(key);
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    public Stream OpenRead(string key)
    {
        return OpenReadAsync(key).GetAwaiter().GetResult();
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var url = BuildUrl(key);
        var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return new ResponseOwnedStream(stream, response);
    }

    public StorageFileInfo? GetInfo(string key)
    {
        return GetInfoAsync(key).GetAwaiter().GetResult();
    }

    public async Task<StorageFileInfo?> GetInfoAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var url = BuildUrl(key);
            var items = await PropfindAsync(url, ct).ConfigureAwait(false);
            var item = items.FirstOrDefault();
            if (item == null)
                return null;

            return new StorageFileInfo
            {
                Key = item.Href,
                Size = item.ContentLength,
                ModifiedUtc = item.LastModified
                , ETag = item.ETag
            };
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private string BuildUrl(string path)
    {
        var cleanPath = path.TrimStart('/');
        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return path;
        var basePath = _basePath.Trim('/');
        if (cleanPath.Length == 0) return basePath.Length == 0 ? _serverUrl + "/" : $"{_serverUrl}/{basePath}/";
        if (basePath.Length > 0 && !cleanPath.Equals(basePath, StringComparison.OrdinalIgnoreCase) &&
            !cleanPath.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase)) cleanPath = $"{basePath}/{cleanPath}";
        return $"{_serverUrl}/{cleanPath}";
    }

    public void Dispose() => _http.Dispose();

    private async Task<List<WebDavItem>> PropfindAsync(string url, CancellationToken ct)
    {
        var propfindBody = @"<?xml version=""1.0"" encoding=""utf-8""?>
<D:propfind xmlns:D=""DAV:"">
  <D:allprop/>
</D:propfind>";

        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), url);
        request.Content = new StringContent(propfindBody, Encoding.UTF8, "application/xml");
        request.Headers.Add("Depth", "1");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        return ParsePropfindResponse(content, url);
    }

    private static List<WebDavItem> ParsePropfindResponse(string xml, string baseUrl)
    {
        var items = new List<WebDavItem>();
        var doc = XDocument.Parse(xml);
            var ns = XNamespace.Get("DAV:");
            var responses = doc.Descendants(ns + "response");

            foreach (var resp in responses)
            {
                var href = resp.Element(ns + "href")?.Value ?? "";
                var props = resp.Element(ns + "propstat")?.Element(ns + "prop");
                if (props == null) continue;

                var resourceType = props.Element(ns + "resourcetype");
                var isCollection = resourceType?.Element(ns + "collection") != null;

                long.TryParse(props.Element(ns + "getcontentlength")?.Value, out var contentLength);
                DateTime.TryParse(props.Element(ns + "getlastmodified")?.Value, out var lastModified);
                var etag = props.Element(ns + "getetag")?.Value;

                items.Add(new WebDavItem
                {
                    Href = href,
                    IsCollection = isCollection,
                    ContentLength = contentLength,
                    LastModified = lastModified
                    , ETag = etag
                });
            }
        return items;
    }

    private static bool UrlsReferToSameResource(string left, string right) =>
        string.Equals(left.TrimEnd('/'), right.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private sealed class WebDavItem
    {
        public string Href { get; set; } = "";
        public bool IsCollection { get; set; }
        public long ContentLength { get; set; }
        public DateTime LastModified { get; set; }
        public string? ETag { get; set; }
    }

    private sealed class ResponseOwnedStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); response.Dispose(); }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
