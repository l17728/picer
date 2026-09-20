using System.Net;
using System.Net.Http;
using Picer.Core.Gateway;

namespace Picer.Tests;

public sealed class HttpIndexGatewayRetryTests
{
    [Fact]
    public async Task HealthRetriesTransientServerFailure()
    {
        using var client = new HttpClient(new FlakyHandler()) { BaseAddress = new Uri("https://index.test/") };
        using var gateway = new HttpIndexGateway("https://index.test", "token", client: client);

        var health = await gateway.GetHealthAsync();

        Assert.Equal("ok", health.Status);
    }

    private sealed class FlakyHandler : HttpMessageHandler
    {
        private int _calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"ok\",\"schemaVersion\":1,\"minClientVersion\":\"1\",\"serviceVersion\":\"test\"}")
            });
        }
    }
}
