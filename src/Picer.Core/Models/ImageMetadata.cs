namespace Picer.Core.Models;

public sealed class ImageMetadata
{
    public int Width { get; init; }
    public int Height { get; init; }
    public DateTime? TakenUtc { get; init; }
    public string? Camera { get; init; }
    public string? Gps { get; init; }
    public string? LocationCountry { get; init; }
    public string? LocationProvince { get; init; }
    public string? LocationCity { get; init; }
    public string? LocationDistrict { get; init; }
}
