using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Metadata;
using Picer.Core.Search;

namespace Picer.Core.Geocoding;

public sealed record LocationEnrichmentProgress(int CompletedGroups, int TotalGroups, int SavedImages, string CurrentPlace);
public sealed record LocationEnrichmentResult(int CoordinateGroups, int SavedImages, int FailedGroups);

public sealed class LocationHierarchyService(PicerDatabase database, IReverseGeocoder geocoder)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<LocationEnrichmentResult> EnrichPendingAsync(
        IProgress<LocationEnrichmentProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var candidates = LoadPending();
            var groups = candidates
                .Select(candidate => GeoPlaceResolver.TryParseCoordinates(candidate.Gps, out var latitude, out var longitude)
                    ? new LocatedCandidate(candidate.ImageId, latitude, longitude, candidate.Path)
                    : null)
                .Where(candidate => candidate is not null).Cast<LocatedCandidate>()
                .GroupBy(candidate => CoordinateKey(candidate.Latitude, candidate.Longitude)).ToArray();
            var saved = 0;
            var failed = 0;
            for (var index = 0; index < groups.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var group = groups[index].ToArray();
                var representative = group[0];
                try
                {
                    var location = ReadEmbeddedLocation(group) ??
                        await geocoder.ReverseAsync(representative.Latitude, representative.Longitude, cancellationToken)
                            .ConfigureAwait(false);
                    if (location is null) { failed++; continue; }
                    Save(group, location);
                    saved += group.Length;
                    progress?.Report(new(index + 1, groups.Length, saved, location.DisplayName));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failed++;
                    progress?.Report(new(index + 1, groups.Length, saved, $"解析失败：{exception.Message}"));
                }
            }
            return new(groups.Length, saved, failed);
        }
        finally
        {
            _gate.Release();
        }
    }

    private IReadOnlyList<PendingLocation> LoadPending()
    {
        using var connection = database.OpenReadConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.id,i.gps,i.path FROM images i
            LEFT JOIN image_locations l ON l.image_id=i.id
            WHERE i.status='active' AND i.gps IS NOT NULL AND i.gps<>'' AND l.image_id IS NULL
            ORDER BY i.id
            """;
        var values = new List<PendingLocation>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) values.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        return values;
    }

    private void Save(IReadOnlyList<LocatedCandidate> candidates, ReverseGeocodingResult location)
    {
        using var connection = database.OpenReadConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var candidate in candidates)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO image_locations(image_id,latitude,longitude,country,province,city,county,district,town,village,display_name,search_text,resolved_utc)
                VALUES($image,$latitude,$longitude,$country,$province,$city,$county,$district,$town,$village,$display,$search,$now)
                ON CONFLICT(image_id) DO UPDATE SET latitude=excluded.latitude,longitude=excluded.longitude,
                    country=excluded.country,province=excluded.province,city=excluded.city,county=excluded.county,
                    district=excluded.district,town=excluded.town,village=excluded.village,
                    display_name=excluded.display_name,search_text=excluded.search_text,resolved_utc=excluded.resolved_utc
                """;
            command.Parameters.AddWithValue("$image", candidate.ImageId);
            command.Parameters.AddWithValue("$latitude", candidate.Latitude);
            command.Parameters.AddWithValue("$longitude", candidate.Longitude);
            command.Parameters.AddWithValue("$country", Db(location.Country));
            command.Parameters.AddWithValue("$province", Db(location.Province));
            command.Parameters.AddWithValue("$city", Db(location.City));
            command.Parameters.AddWithValue("$county", Db(location.County));
            command.Parameters.AddWithValue("$district", Db(location.District));
            command.Parameters.AddWithValue("$town", Db(location.Town));
            command.Parameters.AddWithValue("$village", Db(location.Village));
            command.Parameters.AddWithValue("$display", location.DisplayName);
            command.Parameters.AddWithValue("$search", BuildSearchText(location));
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static string BuildSearchText(ReverseGeocodingResult value) => string.Join(' ',
        new[] { value.Country, value.Province, value.City, value.County, value.District, value.Town, value.Village, value.DisplayName }
            .Where(text => !string.IsNullOrWhiteSpace(text)).Distinct(StringComparer.OrdinalIgnoreCase));

    private static object Db(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    private static ReverseGeocodingResult? ReadEmbeddedLocation(IEnumerable<LocatedCandidate> candidates)
    {
        foreach (var candidate in candidates)
        {
            try
            {
                if (!File.Exists(candidate.Path)) continue;
                var metadata = ImageMetadataReader.Read(candidate.Path);
                var parts = new[] { metadata.LocationDistrict, metadata.LocationCity, metadata.LocationProvince, metadata.LocationCountry }
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (parts.Length == 0) continue;
                return new(string.Join("，", parts), "embedded IPTC metadata",
                    Country: metadata.LocationCountry, Province: metadata.LocationProvince,
                    City: metadata.LocationCity, District: metadata.LocationDistrict, Accuracy: "embedded");
            }
            catch
            {
            }
        }
        return null;
    }
    private static string CoordinateKey(double latitude, double longitude) =>
        FormattableString.Invariant($"{Math.Round(latitude, 3):F3},{Math.Round(longitude, 3):F3}");
    private sealed record PendingLocation(long ImageId, string Gps, string Path);
    private sealed record LocatedCandidate(long ImageId, double Latitude, double Longitude, string Path);
}
