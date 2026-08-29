namespace Picer.Core.Models;

public enum ImageStatus
{
    Active,
    Missing
}

public sealed class ImageRecord
{
    public long Id { get; set; }
    public long SourceId { get; set; }
    public string Path { get; set; } = "";
    public long FileSize { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public DateTime? CreatedUtc { get; set; }
    public string? Sha256 { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime? TakenUtc { get; set; }
    public string? Camera { get; set; }
    public string? Gps { get; set; }
    public string? ThumbPath { get; set; }
    public ImageStatus Status { get; set; } = ImageStatus.Active;
    public DateTime IndexedUtc { get; set; }

    // Perceptual hashes for deduplication
    public long AverageHash { get; set; }
    public long DifferenceHash { get; set; }
}
