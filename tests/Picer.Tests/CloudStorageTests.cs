using System.IO;
using System.Net;
using Picer.Core.Storage;
using Picer.Tests.Stubs;

namespace Picer.Tests;

public sealed class CloudStorageTests : IDisposable
{
    private readonly string _cacheDir;

    public CloudStorageTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), "picer_cloud_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_cacheDir, true); } catch { }
    }

    [Fact]
    public void CachedStorageProvider_CachesOnFirstRead()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "picer_cachedata_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dataDir);

        var file = Path.Combine(dataDir, "test.jpg");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4, 5 });

        var inner = new LocalStorageProvider();
        var cached = new CachedStorageProvider(inner, _cacheDir);

        var stream = cached.OpenRead(file);
        var bytes = new byte[5];
        stream.Read(bytes, 0, 5);
        stream.Close();

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, bytes);

        // File should now be in cache
        var cacheFiles = Directory.EnumerateFiles(_cacheDir).ToList();
        Assert.Single(cacheFiles);

        // Second read should serve from cache
        var stream2 = cached.OpenRead(file);
        var bytes2 = new byte[5];
        stream2.Read(bytes2, 0, 5);
        stream2.Close();
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, bytes2);

        // Cleanup
        Directory.Delete(dataDir, true);
    }

    [Fact]
    public void CachedStorageProvider_CacheClearWorks()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "picer_cachedata_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dataDir);

        var file = Path.Combine(dataDir, "test.jpg");
        File.WriteAllBytes(file, new byte[] { 10, 20, 30 });

        var inner = new LocalStorageProvider();
        var cached = new CachedStorageProvider(inner, _cacheDir);

        // Read to populate cache
        var stream = cached.OpenRead(file);
        stream.Close();

        Assert.True(Directory.EnumerateFiles(_cacheDir).Any());

        // Clear cache
        cached.ClearCache();
        Assert.False(Directory.EnumerateFiles(_cacheDir).Any());

        Directory.Delete(dataDir, true);
    }

    [Fact]
    public async Task CachedStorageProvider_AsyncReadWorks()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "picer_cachedata_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dataDir);

        var file = Path.Combine(dataDir, "async_test.jpg");
        File.WriteAllBytes(file, new byte[] { 100, 200, 150 });

        var inner = new LocalStorageProvider();
        var cached = new CachedStorageProvider(inner, _cacheDir);

        var stream = await cached.OpenReadAsync(file);
        var bytes = new byte[3];
        await stream.ReadAsync(bytes, 0, 3);
        stream.Close();

        Assert.Equal(new byte[] { 100, 200, 150 }, bytes);

        Directory.Delete(dataDir, true);
    }

    [Fact]
    public void CachedStorageProvider_UsesAtomicFilesAndEnforcesCapacity()
    {
        var provider = new InMemoryStorageProvider();
        provider.AddFile("/a.jpg", [1, 2, 3]);
        provider.AddFile("/b.jpg", [4, 5, 6]);
        var cached = new CachedStorageProvider(provider, _cacheDir, maxBytes: 4);

        cached.OpenRead("/a.jpg").Dispose();
        cached.OpenRead("/b.jpg").Dispose();

        var files = Directory.EnumerateFiles(_cacheDir).ToList();
        Assert.Single(files);
        Assert.DoesNotContain(files, path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LocalStorageProvider_Scheme_IsLocal()
    {
        var provider = new LocalStorageProvider();
        Assert.Equal("local", provider.Scheme);
    }

    [Fact]
    public void CachedStorageProvider_DelegatesScheme()
    {
        var inner = new LocalStorageProvider();
        var cached = new CachedStorageProvider(inner, _cacheDir);
        Assert.Equal("local", cached.Scheme);
    }

    [Fact]
    public async Task InMemoryStorageProvider_AsyncMethods_Work()
    {
        var provider = new InMemoryStorageProvider();
        provider.AddFile("/test/photo.jpg", new byte[] { 1, 2, 3 });

        // Async methods should work identically to sync
        Assert.True(await provider.ExistsAsync("/test/photo.jpg"));
        Assert.False(await provider.ExistsAsync("/test/nope.jpg"));

        var info = await provider.GetInfoAsync("/test/photo.jpg");
        Assert.NotNull(info);
        Assert.Equal(3, info!.Size);

        var files = (await provider.EnumerateFilesAsync("/test/")).ToList();
        Assert.Single(files);
    }

    [Fact]
    public async Task DiskBackedStorageProvider_AsyncMethods_Work()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "picer_diskasync_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dataDir);

        var file = Path.Combine(dataDir, "test.jpg");
        File.WriteAllBytes(file, new byte[] { 4, 5, 6 });

        var provider = new DiskBackedStorageProvider();

        Assert.True(await provider.ExistsAsync(file));

        var stream = await provider.OpenReadAsync(file);
        var bytes = new byte[3];
        stream.Read(bytes, 0, 3);
        stream.Close();
        Assert.Equal(new byte[] { 4, 5, 6 }, bytes);

        Directory.Delete(dataDir, true);
    }

    [Fact]
    public void WebDAVStorageProvider_Constructor_NoException()
    {
        // Just verify the constructor doesn't throw
        var provider = new WebDAVStorageProvider(
            "https://example.com/dav/",
            "user",
            "pass",
            "/photos/"
        );
        Assert.Equal("webdav", provider.Scheme);
    }

    [Fact]
    public async Task WebDAVStorageProvider_EnumerationFailureIsNotReportedAsEmptyLibrary()
    {
        var provider = new WebDAVStorageProvider("https://example.test", "", "", "/",
            new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.EnumerateFilesAsync("/photos"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task WebDAVStorageProvider_PermissionErrorsArePropagated(HttpStatusCode status)
    {
        var provider = new WebDAVStorageProvider("https://example.test", "", "", "/",
            new StubHttpHandler(_ => new HttpResponseMessage(status)));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.EnumerateFilesAsync("/photos"));
        Assert.Equal(status, error.StatusCode);
    }

    [Fact]
    public async Task WebDAVStorageProvider_ExistsOnlyTreatsNotFoundAsMissing()
    {
        var missing = new WebDAVStorageProvider("https://example.test", "", "", "/",
            new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.False(await missing.ExistsAsync("/gone.jpg"));

        var failed = new WebDAVStorageProvider("https://example.test", "", "", "/",
            new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        await Assert.ThrowsAsync<HttpRequestException>(() => failed.ExistsAsync("/unknown.jpg"));
    }

    [Fact]
    public async Task WebDAVStorageProvider_AppliesConfiguredBasePathToRelativeKeys()
    {
        Uri? requested = null;
        using var provider = new WebDAVStorageProvider("https://example.test/dav", "", "", "/photos",
            new StubHttpHandler(request => { requested = request.RequestUri; return new HttpResponseMessage(HttpStatusCode.OK); }));

        Assert.True(await provider.ExistsAsync("holiday/photo.jpg"));

        Assert.Equal("https://example.test/dav/photos/holiday/photo.jpg", requested!.AbsoluteUri);
    }

    [Fact]
    public async Task WebDAVStorageProvider_TimeoutCancellationStopsRequest()
    {
        var provider = new WebDAVStorageProvider("https://example.test", "", "", "/",
            new AsyncStubHttpHandler(async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.EnumerateFilesAsync("/photos", cancellation.Token));
    }

    [Fact]
    public async Task WebDAVStorageProvider_StreamsBodyAfterHundredMillisecondRtt()
    {
        var provider = new WebDAVStorageProvider("https://example.test", "", "", "/",
            new AsyncStubHttpHandler(async (_, ct) =>
            {
                await Task.Delay(100, ct);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            }));

        await using var stream = await provider.OpenReadAsync("/photo.jpg");
        var bytes = new byte[3];
        Assert.Equal(3, await stream.ReadAsync(bytes));
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
    }

    [Fact]
    public void CachedStorageProvider_ETagInvalidatesSameSizeAndTimestamp()
    {
        var inner = new ETagStorageProvider();
        var cached = new CachedStorageProvider(inner, _cacheDir);
        using (var first = cached.OpenRead("/photo.jpg")) Assert.Equal(1, first.ReadByte());
        inner.Update(2, "v2");

        using var second = cached.OpenRead("/photo.jpg");
        Assert.Equal(2, second.ReadByte());
        Assert.Equal(2, inner.OpenCount);
    }

    [Fact]
    public void StorageConfig_DefaultIsLocal()
    {
        var config = new StorageConfig();
        Assert.Equal("local", config.Type);
        Assert.Null(config.WebDAV);
    }

    [Fact]
    public void StorageConfig_CreateProvider_Local()
    {
        var config = new StorageConfig { Type = "local" };
        var provider = config.CreateProvider(_cacheDir);
        Assert.IsType<LocalStorageProvider>(provider);
    }

    [Fact]
    public void StorageConfig_CreateProvider_LocalWithCache()
    {
        var config = new StorageConfig
        {
            Type = "local",
            Cache = new CacheConfig { Enabled = true, MaxSizeGb = 17 }
        };
        var provider = config.CreateProvider(_cacheDir);
        Assert.IsType<CachedStorageProvider>(provider);
    }

    [Fact]
    public void StorageConfig_SaveAndLoad()
    {
        var configPath = Path.Combine(_cacheDir, "storage.json");
        var config = new StorageConfig
        {
            Type = "webdav",
            WebDAV = new WebDAVConfig
            {
                ServerUrl = "https://nas.example.com/dav/",
                Username = "admin",
                Password = "secret",
                BasePath = "/photos"
            },
            Cache = new CacheConfig { Enabled = true, MaxSizeGb = 17 }
        };

        config.Save(configPath);
        Assert.True(File.Exists(configPath));
        Assert.DoesNotContain("secret", File.ReadAllText(configPath));

        var loaded = StorageConfig.Load(configPath);
        Assert.Equal("webdav", loaded.Type);
        Assert.NotNull(loaded.WebDAV);
        Assert.Equal("https://nas.example.com/dav/", loaded.WebDAV!.ServerUrl);
        Assert.Equal("admin", loaded.WebDAV.Username);
        Assert.Equal("/photos", loaded.WebDAV.BasePath);
        Assert.True(loaded.Cache!.Enabled);
        Assert.Equal(17, loaded.Cache.MaxSizeGb);
        Assert.Equal("", loaded.WebDAV.Password);
    }

    [Fact]
    public void StorageConfig_ReadsLegacyPasswordForCredentialMigration()
    {
        var configPath = Path.Combine(_cacheDir, "legacy-storage.json");
        File.WriteAllText(configPath, """{"type":"webdav","webdav":{"server_url":"https://dav.example","username":"user","password":"legacy-secret","base_path":"/photos"}}""");

        Assert.Equal("legacy-secret", StorageConfig.ReadLegacyWebDavPassword(configPath));
    }
}

internal sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(response(request));
}

internal sealed class AsyncStubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        response(request, cancellationToken);
}

internal sealed class ETagStorageProvider : IStorageProvider
{
    private byte _value = 1;
    private string _etag = "v1";
    private readonly DateTime _modified = DateTime.UtcNow;
    public int OpenCount { get; private set; }
    public string Scheme => "etag";
    public void Update(byte value, string etag) { _value = value; _etag = etag; }
    public IEnumerable<StorageFileInfo> EnumerateFiles(string root) => [GetInfo("/photo.jpg")!];
    public Task<IEnumerable<StorageFileInfo>> EnumerateFilesAsync(string root, CancellationToken ct = default) => Task.FromResult(EnumerateFiles(root));
    public bool Exists(string key) => true;
    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(true);
    public Stream OpenRead(string key) { OpenCount++; return new MemoryStream([_value]); }
    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) => Task.FromResult(OpenRead(key));
    public StorageFileInfo? GetInfo(string key) => new() { Key = key, Size = 1, ModifiedUtc = _modified, ETag = _etag };
    public Task<StorageFileInfo?> GetInfoAsync(string key, CancellationToken ct = default) => Task.FromResult(GetInfo(key));
}
