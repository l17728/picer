using System.Diagnostics.Tracing;

namespace Picer.Core.Diagnostics;

[EventSource(Name = "Picer-Core")]
public sealed class PicerEventSource : EventSource
{
    public static readonly PicerEventSource Log = new();

    private readonly EventCounter _searchDuration;
    private readonly IncrementingEventCounter _indexedFiles;

    private PicerEventSource()
    {
        _searchDuration = new EventCounter("search-duration-ms", this) { DisplayName = "Search duration", DisplayUnits = "ms" };
        _indexedFiles = new IncrementingEventCounter("indexed-files", this) { DisplayName = "Indexed files", DisplayRateTimeScale = TimeSpan.FromSeconds(1) };
    }

    [NonEvent]
    public void SearchCompleted(double elapsedMilliseconds, int resultCount)
    {
        _searchDuration.WriteMetric((float)elapsedMilliseconds);
        SearchResult(resultCount);
    }

    [Event(1, Level = EventLevel.Informational)]
    public void StartupStage(string stage, double elapsedMilliseconds) => WriteEvent(1, stage, elapsedMilliseconds);

    [Event(2, Level = EventLevel.Informational)]
    public void SearchResult(int resultCount) => WriteEvent(2, resultCount);

    [NonEvent]
    public void FileIndexed() => _indexedFiles.Increment();

    protected override void Dispose(bool disposing)
    {
        _searchDuration.Dispose();
        _indexedFiles.Dispose();
        base.Dispose(disposing);
    }
}
