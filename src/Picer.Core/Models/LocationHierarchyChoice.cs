namespace Picer.Core.Models;

public sealed record LocationHierarchyChoice(
    string DisplayName,
    string? Country = null,
    string? Province = null,
    string? City = null,
    string? County = null,
    string? District = null,
    string? Town = null,
    string? Village = null)
{
    public LocationHierarchyChoice AddChild(string child)
    {
        child = child.Trim();
        var updated = string.IsNullOrEmpty(Country) ? this with { Country = child }
            : string.IsNullOrEmpty(Province) ? this with { Province = child }
            : string.IsNullOrEmpty(City) ? this with { City = child }
            : string.IsNullOrEmpty(County) ? this with { County = child }
            : string.IsNullOrEmpty(District) ? this with { District = child }
            : string.IsNullOrEmpty(Town) ? this with { Town = child }
            : string.IsNullOrEmpty(Village) ? this with { Village = child }
            : this with { Village = Village + " / " + child };
        return updated with { DisplayName = string.Join(" / ", new[] { updated.Country, updated.Province, updated.City,
            updated.County, updated.District, updated.Town, updated.Village }.Where(value => !string.IsNullOrWhiteSpace(value))) };
    }
}
