using Picer.Core.Gateway;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Picer.Tests;

[Collection(nameof(GatewayContractCollection))]
public sealed class MirrorChangeContractTests(GatewayServiceFixture fixture) : IClassFixture<GatewayServiceFixture>
{
    [Fact]
    public async Task HttpChangeFeedRetriesTransientServiceUnavailable()
    {
        var handler = new RetryOnceHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://index.test/") };
        using var gateway = new HttpIndexGateway(client);

        var page = await gateway.GetChangesAsync(new());

        Assert.Equal(2, handler.Attempts);
        Assert.Equal("current", page.Epoch);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("http")]
    [InlineData("pipe")]
    public async Task EveryTransportPreservesPageBoundsAndWatermarks(string transport)
    {
        using var gateway = fixture.CreateGateway(transport);
        var page = await gateway.GetChangesAsync(new(PageSize: 2));
        Assert.False(page.ResetRequired);
        Assert.Equal(2, page.Items.Count);
        Assert.True(page.HasMore);
        var epoch = page.Epoch;
        var through = page.Through;
        var ids = page.Items.Select(i => i.Id).ToList();
        while (page.HasMore)
        {
            page = await gateway.GetChangesAsync(new(epoch, page.Next, through, 2));
            Assert.Equal(epoch, page.Epoch);
            Assert.Equal(through, page.Through);
            ids.AddRange(page.Items.Select(i => i.Id));
        }
        Assert.Equal(5, ids.Distinct().Count());
        Assert.Equal(through, page.Next);
        var unchanged = await gateway.GetChangesAsync(new(epoch, page.Next));
        Assert.Empty(unchanged.Items);
        Assert.False(unchanged.HasMore);
        Assert.True((await gateway.GetChangesAsync(new("previous-server", 0))).ResetRequired);
    }

    private sealed class RetryOnceHandler : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts == 1)
            {
                var busy = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                busy.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(busy);
            }
            var page = new GatewayChangePage("current", 0, 0, false, false, 0, []);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(page), Encoding.UTF8, "application/json")
            });
        }
    }
}
