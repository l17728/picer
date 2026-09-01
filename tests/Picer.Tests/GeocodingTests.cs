using System.Net;
using System.Text;
using Picer.Core.Geocoding;
using Picer.Core.Search;

namespace Picer.Tests;

public sealed class GeocodingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "picer-geocode-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("商州")]
    [InlineData("商州市")]
    [InlineData("商州区")]
    [InlineData("商洛市商州区")]
    public void PlaceAliases_ResolveToSameAdministrativeArea(string query)
    {
        var bounds = GeoPlaceResolver.Resolve(query);
        Assert.NotNull(bounds);
        Assert.Equal("商洛市商州区", bounds.Name);
        Assert.True(GeoPlaceResolver.IsWithin("33.87,109.94", bounds.MinLatitude, bounds.MaxLatitude, bounds.MinLongitude, bounds.MaxLongitude));
    }

    [Fact]
    public void MelbourneSearch_HandlesLegacyDmsWithoutSouthernReference()
    {
        var bounds = GeoPlaceResolver.Resolve("墨尔本");
        Assert.NotNull(bounds);
        Assert.True(GeoPlaceResolver.IsWithin(
            "37° 48' 49.0\", 144° 57' 47.0\"",
            bounds.MinLatitude, bounds.MaxLatitude, bounds.MinLongitude, bounds.MaxLongitude));
    }

    [Theory]
    [InlineData("澳大利亚")]
    [InlineData("澳洲")]
    [InlineData("Australia")]
    public void AustraliaSearch_IncludesMelbourne(string query)
    {
        var bounds = GeoPlaceResolver.Resolve(query);
        Assert.NotNull(bounds);
        Assert.Equal("澳大利亚", bounds.Name);
        Assert.True(GeoPlaceResolver.IsWithin("-37.8136,144.9631",
            bounds.MinLatitude, bounds.MaxLatitude, bounds.MinLongitude, bounds.MaxLongitude));
        Assert.False(GeoPlaceResolver.IsWithin("34.3416,108.9398",
            bounds.MinLatitude, bounds.MaxLatitude, bounds.MinLongitude, bounds.MaxLongitude));
    }

    [Theory]
    [InlineData("商洛")]
    [InlineData("商洛市")]
    public void ShangluoAliases_ResolveToCityBounds(string query)
    {
        var bounds = GeoPlaceResolver.Resolve(query);
        Assert.NotNull(bounds);
        Assert.Equal("商洛市", bounds.Name);
        Assert.True(GeoPlaceResolver.IsWithin("33.87,109.94", bounds.MinLatitude, bounds.MaxLatitude, bounds.MinLongitude, bounds.MaxLongitude));
    }

    [Fact]
    public async Task ReverseGeocoder_CachesResultAndSendsIdentifyingUserAgent()
    {
        Directory.CreateDirectory(_directory);
        var handler = new StubHandler();
        using var client = new HttpClient(handler);
        var cachePath = Path.Combine(_directory, "locations.json");
        using (var geocoder = new NominatimReverseGeocoder(cachePath, client))
        {
            var first = await geocoder.ReverseAsync(34.3416, 108.9398);
            var second = await geocoder.ReverseAsync(34.3416, 108.9398);
            Assert.Equal("西安市, 陕西省, 中国", first?.DisplayName);
            Assert.Equal(first, second);
        }

        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("Picer/1.0", handler.UserAgent);
        Assert.True(File.Exists(cachePath));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string UserAgent { get; private set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            UserAgent = request.Headers.UserAgent.ToString();
            const string json = "{\"display_name\":\"西安市, 陕西省, 中国\",\"licence\":\"© OpenStreetMap contributors\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
