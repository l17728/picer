namespace Picer.Core.Models;

public sealed class ImageMetadata
{
    public int Width { get; init; }
    public int Height { get; init; }
    public DateTime? TakenUtc { get; init; }
    public string? Camera { get; init; }
    public string? Gps { get; init; }
}
