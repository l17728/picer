using Picer.Core.Gateway;

namespace Picer.Tests;

[Collection(nameof(GatewayContractCollection))]
public sealed class MirrorChangeContractTests(GatewayServiceFixture fixture) : IClassFixture<GatewayServiceFixture>
{
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
}
