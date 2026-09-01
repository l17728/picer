using System.Globalization;
using System.Text.RegularExpressions;

namespace Picer.Core.Search;

public sealed record GeoBounds(string Name, double MinLatitude, double MaxLatitude, double MinLongitude, double MaxLongitude);

public static partial class GeoPlaceResolver
{
    private static readonly IReadOnlyDictionary<string, GeoBounds> Places =
        new Dictionary<string, GeoBounds>(StringComparer.OrdinalIgnoreCase)
        {
            ["西安"] = new("西安", 33.70, 34.80, 107.60, 109.85),
            ["西安市"] = new("西安", 33.70, 34.80, 107.60, 109.85),
            ["北京"] = new("北京", 39.40, 41.10, 115.40, 117.60),
            ["上海"] = new("上海", 30.65, 31.90, 120.85, 122.25),
            ["广州"] = new("广州", 22.40, 23.95, 112.95, 114.10),
            ["深圳"] = new("深圳", 22.40, 22.90, 113.70, 114.65),
            ["成都"] = new("成都", 30.05, 31.45, 102.90, 104.90),
            ["杭州"] = new("杭州", 29.15, 30.60, 118.30, 120.75),
            ["南京"] = new("南京", 31.20, 32.65, 118.20, 119.25),
            ["武汉"] = new("武汉", 29.95, 31.40, 113.65, 115.10),
            ["重庆"] = new("重庆", 28.15, 32.20, 105.25, 110.20)
            , ["墨尔本"] = new("墨尔本", -38.55, -37.35, 144.35, 145.80)
            , ["墨尔本市"] = new("墨尔本", -38.55, -37.35, 144.35, 145.80)
            , ["Melbourne"] = new("墨尔本", -38.55, -37.35, 144.35, 145.80)
            , ["澳大利亚"] = new("澳大利亚", -44.00, -10.00, 112.00, 154.00)
            , ["澳洲"] = new("澳大利亚", -44.00, -10.00, 112.00, 154.00)
            , ["Australia"] = new("澳大利亚", -44.00, -10.00, 112.00, 154.00)
            , ["陕西"] = new("陕西省", 31.70, 37.20, 105.30, 111.25)
            , ["陕西省"] = new("陕西省", 31.70, 37.20, 105.30, 111.25)
            , ["商州"] = new("商洛市商州区", 33.48, 34.12, 109.45, 110.25)
            , ["商州市"] = new("商洛市商州区", 33.48, 34.12, 109.45, 110.25)
            , ["商州区"] = new("商洛市商州区", 33.48, 34.12, 109.45, 110.25)
            , ["商洛市商州区"] = new("商洛市商州区", 33.48, 34.12, 109.45, 110.25)
            , ["商洛"] = new("商洛市", 32.95, 34.42, 108.55, 111.10)
            , ["商洛市"] = new("商洛市", 32.95, 34.42, 108.55, 111.10)
        };

    public static GeoBounds? Resolve(string? query) =>
        string.IsNullOrWhiteSpace(query) ? null : Places.GetValueOrDefault(query.Trim());

    public static bool IsWithin(string? gps, double minLatitude, double maxLatitude, double minLongitude, double maxLongitude)
    {
        if (!TryParseCoordinates(gps, out var latitude, out var longitude)) return false;
        // Older indexes stored EXIF DMS descriptions without their N/S or E/W
        // reference. Infer the sign only when the requested region lies wholly
        // in a negative hemisphere; newly indexed decimal coordinates retain it.
        if (maxLatitude < 0 && latitude > 0 && !HasExplicitDirection(gps!, 'N', 'S')) latitude = -latitude;
        if (maxLongitude < 0 && longitude > 0 && !HasExplicitDirection(gps!, 'E', 'W')) longitude = -longitude;
        return latitude >= minLatitude && latitude <= maxLatitude &&
               longitude >= minLongitude && longitude <= maxLongitude;
    }

    private static bool HasExplicitDirection(string gps, char positive, char negative) =>
        gps.Contains('-') || gps.Contains(positive.ToString(), StringComparison.OrdinalIgnoreCase) ||
        gps.Contains(negative.ToString(), StringComparison.OrdinalIgnoreCase);

    public static bool TryParseCoordinates(string? gps, out double latitude, out double longitude)
    {
        latitude = longitude = 0;
        if (string.IsNullOrWhiteSpace(gps)) return false;
        var parts = gps.Split(',', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 && TryParseCoordinate(parts[0], true, out latitude) &&
               TryParseCoordinate(parts[1], false, out longitude);
    }

    private static bool TryParseCoordinate(string value, bool latitude, out double coordinate)
    {
        coordinate = 0;
        var matches = NumberPattern().Matches(value);
        if (matches.Count == 0) return false;
        var numbers = matches.Cast<Match>().Select(match => double.Parse(match.Value, CultureInfo.InvariantCulture)).ToArray();
        coordinate = numbers[0] + (numbers.Length > 1 ? numbers[1] / 60d : 0) + (numbers.Length > 2 ? numbers[2] / 3600d : 0);
        if (value.Contains('-') || value.Contains((latitude ? "S" : "W"), StringComparison.OrdinalIgnoreCase))
            coordinate = -coordinate;
        return latitude ? coordinate is >= -90 and <= 90 : coordinate is >= -180 and <= 180;
    }

    [GeneratedRegex(@"\d+(?:\.\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();
}
