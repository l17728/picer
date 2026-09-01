using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Picer.Core.Search;

namespace Picer.Core.Geocoding;

public sealed record ReverseGeocodingResult(
    string DisplayName,
    string Attribution,
    string? Country = null,
    string? Province = null,
    string? City = null,
    string? District = null,
    string? Poi = null,
    IReadOnlyList<Picer.Core.Vision.LocationLandmark>? NearbyLandmarks = null,
    string Accuracy = "estimated",
    string? County = null,
    string? Town = null,
    string? Village = null);

public interface IReverseGeocoder
{
    Task<ReverseGeocodingResult?> ReverseAsync(double latitude, double longitude, CancellationToken cancellationToken = default);
    Task<GeoBounds?> ResolvePlaceAsync(string query, CancellationToken cancellationToken = default);
}

public sealed class NominatimReverseGeocoder : IReverseGeocoder, IDisposable
{
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static DateTime _lastRequestUtc = DateTime.MinValue;
    private readonly HttpClient _client;
    private readonly string _cachePath;
    private readonly Dictionary<string, ReverseGeocodingResult> _cache;
    private readonly Dictionary<string, GeoBounds> _placeCache;
    private readonly bool _ownsClient;

    public NominatimReverseGeocoder(string cachePath, HttpClient? client = null)
    {
        _cachePath = cachePath;
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _ownsClient = client is null;
        _client.DefaultRequestHeaders.UserAgent.Clear();
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Picer", "1.0"));
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(desktop-photo-manager)"));
        _cache = LoadCache(cachePath);
        _placeCache = LoadPlaceCache(cachePath + ".places");
    }

    public async Task<GeoBounds?> ResolvePlaceAsync(string query, CancellationToken cancellationToken = default)
    {
        var key = query.Trim();
        if (key.Length == 0) return null;
        if (_placeCache.TryGetValue(key, out var cached)) return cached;
        await RequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_placeCache.TryGetValue(key, out cached)) return cached;
            var delay = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - _lastRequestUtc);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            var uri = "https://nominatim.openstreetmap.org/search?format=jsonv2&addressdetails=1&limit=1&accept-language=zh-CN&q=" +
                      Uri.EscapeDataString(key);
            _lastRequestUtc = DateTime.UtcNow;
            using var response = await _client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() == 0) return null;
            var item = json.RootElement[0];
            if (!item.TryGetProperty("boundingbox", out var box) || box.GetArrayLength() != 4) return null;
            var name = item.TryGetProperty("display_name", out var display) ? display.GetString() ?? key : key;
            var result = new GeoBounds(name,
                ParseNumber(box[0]), ParseNumber(box[1]), ParseNumber(box[2]), ParseNumber(box[3]));
            _placeCache[key] = result;
            SavePlaceCache();
            return result;
        }
        finally { RequestGate.Release(); }
    }

    public async Task<ReverseGeocodingResult?> ReverseAsync(double latitude, double longitude, CancellationToken cancellationToken = default)
    {
        var key = FormattableString.Invariant($"{latitude:F4},{longitude:F4}");
        if (_cache.TryGetValue(key, out var cached)) return NormalizeAdministrativeHierarchy(cached);

        await RequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(key, out cached)) return NormalizeAdministrativeHierarchy(cached);
            var delay = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - _lastRequestUtc);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            var uri = "https://nominatim.openstreetmap.org/reverse?format=jsonv2&addressdetails=1&zoom=18&layer=address" +
                      $"&accept-language=zh-CN&lat={latitude.ToString("R", CultureInfo.InvariantCulture)}" +
                      $"&lon={longitude.ToString("R", CultureInfo.InvariantCulture)}";
            _lastRequestUtc = DateTime.UtcNow;
            using var response = await _client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!json.RootElement.TryGetProperty("display_name", out var displayName) || string.IsNullOrWhiteSpace(displayName.GetString()))
                return null;
            var attribution = json.RootElement.TryGetProperty("licence", out var licence)
                ? licence.GetString() ?? "© OpenStreetMap contributors"
                : "© OpenStreetMap contributors";
            var address = json.RootElement.TryGetProperty("address", out var addressValue) ? addressValue : default;
            string? Address(params string[] names)
            {
                if (address.ValueKind != JsonValueKind.Object) return null;
                foreach (var name in names)
                    if (address.TryGetProperty(name, out var value) && !string.IsNullOrWhiteSpace(value.GetString()))
                        return value.GetString();
                return null;
            }
            var result = new ReverseGeocodingResult(
                displayName.GetString()!, attribution,
                Country: Address("country"),
                Province: Address("state", "province", "region"),
                City: Address("city"),
                District: Address("city_district", "district", "borough", "suburb"),
                Poi: Address("tourism", "attraction", "amenity", "historic", "leisure"),
                County: Address("county"),
                Town: Address("town", "municipality", "township"),
                Village: Address("village", "hamlet", "neighbourhood", "quarter"));
            result = NormalizeAdministrativeHierarchy(result);
            _cache[key] = result;
            SaveCache();
            return result;
        }
        finally
        {
            RequestGate.Release();
        }
    }

    private static ReverseGeocodingResult NormalizeAdministrativeHierarchy(ReverseGeocodingResult value)
    {
        if (!string.Equals(value.Country, "中国", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(value.City) ||
            (!value.City.EndsWith('区') && !value.City.EndsWith('县')))
            return value;

        var administrativeCity = value.DisplayName.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.EndsWith('市') && !string.Equals(part, value.City, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(administrativeCity)) return value;

        return value with
        {
            City = administrativeCity,
            County = value.City.EndsWith('县') ? value.City : value.County,
            District = value.City.EndsWith('区') ? value.City : value.District
        };
    }

    private static Dictionary<string, ReverseGeocodingResult> LoadCache(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, ReverseGeocodingResult>>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch { return new(); }
    }

    private static Dictionary<string, GeoBounds> LoadPlaceCache(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, GeoBounds>>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch { return new(); }
    }

    private static double ParseNumber(JsonElement element) =>
        double.Parse(element.GetString()!, CultureInfo.InvariantCulture);

    private void SaveCache()
    {
        var directory = Path.GetDirectoryName(_cachePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = _cachePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_cache));
        File.Move(temporary, _cachePath, true);
    }

    private void SavePlaceCache()
    {
        var path = _cachePath + ".places";
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_placeCache));
        File.Move(temporary, path, true);
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}
