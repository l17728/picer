using System.Net.Http;
using Picer.MediaAgent;

namespace Picer.E2E;

public sealed class ModelRequestAuthorizationTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8091/api/face-inference")]
    [InlineData("https://localhost/api/face-inference")]
    [InlineData("http://[::1]:8091/api/face-inference")]
    public void LocalService_UsesExistingMachineCredential(string endpoint)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        ModelRequestAuthorization.Apply(request, " ", () => " local-test-key ");
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("local-test-key", request.Headers.Authorization?.Parameter);
    }

    [Theory]
    [InlineData("https://example.invalid/api/face")]
    [InlineData("http://localhost.example.invalid/api/face")]
    [InlineData("api/face")]
    public void RemoteOrRelativeEndpoint_DoesNotReadOrSendMachineCredential(string endpoint)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        ModelRequestAuthorization.Apply(request, null, () => throw new InvalidOperationException("Credential must not be read"));
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public void ExplicitAgentCredential_TakesPrecedence()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:8091/api/face-inference");
        ModelRequestAuthorization.Apply(request, " explicit-key ", () => throw new InvalidOperationException("Fallback must not be read"));
        Assert.Equal("explicit-key", request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public void MissingCredential_DoesNotAddEmptyAuthorization()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:8091/api/face-inference");
        ModelRequestAuthorization.Apply(request, null, () => " ");
        Assert.Null(request.Headers.Authorization);
    }
}
