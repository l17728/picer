using System.Text;
using System.Text.Json;
using Picer.Core.Gateway;

namespace Picer.Tests;

public sealed class IndexBackendConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-backend-" + Guid.NewGuid().ToString("N"));
    private string BackendPath => Path.Combine(_root, "backend.json");
    private string EndpointPath => Path.Combine(_root, "service-endpoint.json");

    public IndexBackendConfigTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void MissingFiles_DefaultsToLocalMode()
    {
        var options = IndexBackendConfig.Load(BackendPath, EndpointPath);
        Assert.Equal(IndexBackendConfig.LocalMode, options.Mode);
        Assert.Null(options.BaseUrl);
        Assert.Null(options.Token);
        Assert.Null(options.PipeName);
    }

    [Fact]
    public void LocalMode_IgnoresEndpointFile()
    {
        File.WriteAllText(BackendPath, """{"mode":"local"}""");
        File.WriteAllText(EndpointPath, """{"url":"https://127.0.0.1:8445","spkiPin":"AAEC","tokenCipher":"AAEC","pipeName":"picer-x"}""");
        var options = IndexBackendConfig.Load(BackendPath, EndpointPath);
        Assert.Equal(IndexBackendConfig.LocalMode, options.Mode);
    }

    [Fact]
    public void ServiceMode_ResolvesEndpointFields()
    {
        File.WriteAllText(BackendPath, """{"mode":"service"}""");
        File.WriteAllText(EndpointPath, $$"""
            {"url":"https://127.0.0.1:8445","spkiPin":"{{Convert.ToBase64String([1, 2, 3, 4])}}","tokenCipher":"{{Convert.ToBase64String(Encoding.UTF8.GetBytes("tok"))}}","pipeName":"picer-test-pipe"}
            """);
        var options = IndexBackendConfig.Load(BackendPath, EndpointPath, static cipher => cipher);
        Assert.Equal(IndexBackendConfig.ServiceMode, options.Mode);
        Assert.Equal("https://127.0.0.1:8445", options.BaseUrl);
        Assert.Equal("picer-test-pipe", options.PipeName);
        Assert.Equal("tok", options.Token);
        Assert.Equal([1, 2, 3, 4], options.SpkiPin);
    }

    [Fact]
    public void ServiceMode_BackendBaseUrl_OverridesEndpointUrl()
    {
        File.WriteAllText(BackendPath, """{"mode":"service","baseUrl":"https://nas:8445"}""");
        File.WriteAllText(EndpointPath, """{"url":"https://127.0.0.1:8445","spkiPin":"AAEC","tokenCipher":"AAEC","pipeName":"picer-x"}""");
        var options = IndexBackendConfig.Load(BackendPath, EndpointPath, static cipher => cipher);
        Assert.Equal("https://nas:8445", options.BaseUrl);
        Assert.Equal("picer-x", options.PipeName);
    }

    [Fact]
    public void ServiceMode_IndexServiceIp_ComposesFixedHttpsEndpoint()
    {
        File.WriteAllText(BackendPath, """{"mode":"service","indexServiceIp":"124.156.193.122"}""");
        File.WriteAllText(EndpointPath, """{"url":"https://127.0.0.1:8445","spkiPin":"AAEC","tokenCipher":"AAEC","pipeName":"picer-x"}""");
        var options = IndexBackendConfig.Load(BackendPath, EndpointPath, static cipher => cipher);
        Assert.Equal("https://124.156.193.122:8445", options.BaseUrl);
    }

    [Fact]
    public void EnvironmentVariable_OverridesCipherToken()
    {
        File.WriteAllText(BackendPath, """{"mode":"service"}""");
        File.WriteAllText(EndpointPath, """{"url":"https://127.0.0.1:8445","spkiPin":"AAEC","tokenCipher":"AAEC","pipeName":"picer-x"}""");
        Environment.SetEnvironmentVariable("PICER_INDEX_SERVICE_TOKEN", "env-token");
        try
        {
            var options = IndexBackendConfig.Load(BackendPath, EndpointPath);
            Assert.Equal("env-token", options.Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PICER_INDEX_SERVICE_TOKEN", null);
        }
    }

    [Fact]
    public void MalformedBackendJson_FallsBackToLocalMode()
    {
        File.WriteAllText(BackendPath, "{ this is not json");
        var options = IndexBackendConfig.Load(BackendPath, EndpointPath);
        Assert.Equal(IndexBackendConfig.LocalMode, options.Mode);
    }

    [Fact]
    public void ServiceMode_MissingEndpointFile_Throws()
    {
        File.WriteAllText(BackendPath, """{"mode":"service"}""");
        Assert.Throws<InvalidOperationException>(() => IndexBackendConfig.Load(BackendPath, null));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
