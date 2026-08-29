namespace Picer.Core.Models;

public sealed class SourceFolder
{
    public long Id { get; set; }
    public string Path { get; set; } = "";
    public DateTime AddedUtc { get; set; }
    public int ImageCount { get; set; }
}
