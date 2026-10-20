namespace Picer.Core.Search;

public enum SortDirection
{
    Ascending,
    Descending
}

public enum SortField
{
    FileName,
    TakenDate,
    Width,
    Height,
    FileSize,
    IndexedTime
}

public enum TimeOfDayFilter
{
    Any,
    Day,
    Night
}

public sealed class SearchFilters
{
    public string Query { get; set; } = "";
    public long? SourceId { get; set; } = null;
    public bool IncludeMissing { get; set; } = true;

    // EXIF/date filters
    public DateTime? TakenAfter { get; set; } = null;
    public DateTime? TakenBefore { get; set; } = null;
    public TimeOfDayFilter TimeOfDay { get; set; } = TimeOfDayFilter.Any;

    // Dimension filters
    public int? MinWidth { get; set; } = null;
    public int? MaxWidth { get; set; } = null;
    public int? MinHeight { get; set; } = null;
    public int? MaxHeight { get; set; } = null;

    // File size filter (in bytes)
    public long? MinFileSize { get; set; } = null;
    public long? MaxFileSize { get; set; } = null;

    // Camera/brand filter
    public string? CameraBrand { get; set; } = null;
    public long? TagId { get; set; } = null;
    public GeoBounds? GeoBounds { get; set; } = null;
    public long? PersonId { get; set; } = null;
    public IReadOnlyList<string> VisualConceptIds { get; set; } = [];
    public bool IncludeSuggestedClassifications { get; set; }

    public SortField SortBy { get; set; } = SortField.TakenDate;
    public SortDirection SortDir { get; set; } = SortDirection.Ascending;

    /// <summary>Returns a short label for the active filters.</summary>
    public string FilterSummary => BuildFilterSummary();

    private string BuildFilterSummary()
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(CameraBrand))
            parts.Add($"相机: {CameraBrand}");
        if (TagId.HasValue) parts.Add($"标签ID: {TagId}");
        if (TakenAfter.HasValue)
            parts.Add($"拍摄 ≥ {TakenAfter.Value:yyyy-MM-dd}");
        if (TakenBefore.HasValue)
            parts.Add($"拍摄 ≤ {TakenBefore.Value:yyyy-MM-dd}");
        if (MinWidth.HasValue || MaxWidth.HasValue)
        {
            var w = $"{(MinWidth ?? 0)}-{(MaxWidth.HasValue ? MaxWidth.ToString() : "∞")}";
            parts.Add($"宽度: {w}");
        }
        if (MinHeight.HasValue || MaxHeight.HasValue)
        {
            var h = $"{(MinHeight ?? 0)}-{(MaxHeight.HasValue ? MaxHeight.ToString() : "∞")}";
            parts.Add($"高度: {h}");
        }
        return string.Join(" | ", parts);
    }
}
