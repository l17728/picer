namespace Picer.Core.Search;

/// <summary>
/// Represents a tag that can be assigned to images.
/// Purely logical — does not move files on disk.
/// </summary>
public sealed class Tag
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// Represents an album (smart or manual collection) of images.
/// Purely logical — does not move files on disk.
/// </summary>
public sealed class Album
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public long? CreatorId { get; set; } // which source created it
    public DateTime CreatedUtc { get; set; }
    public string? Description { get; set; }
}
