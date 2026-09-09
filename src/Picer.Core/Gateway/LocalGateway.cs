using Picer.Core.Data;
using Picer.Core.Indexing;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Thumbnails;

namespace Picer.Core.Gateway;

public sealed class LocalGateway(PicerDatabase db, ThumbnailService thumbnails, IndexEngine? engine = null) : IIndexGateway
{
    private ScanJobManager? _jobs;

    public void Dispose() { }
    public Task<GatewayHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new GatewayHealth("ok", PicerDatabase.CurrentSchemaVersion, "1.0", typeof(LocalGateway).Assembly.GetName().Version?.ToString() ?? "unknown"));

    public Task<GatewayPage> ListImagesAsync(long? sourceId = null, bool includeMissing = false, int pageSize = 200, string? cursor = null, CancellationToken cancellationToken = default)
    {
        var filters = new SearchFilters { SourceId = sourceId, IncludeMissing = includeMissing };
        return SearchAsync(filters, pageSize, cursor, cancellationToken);
    }

    public Task<ImageRecord?> GetImageAsync(long id, CancellationToken cancellationToken = default) =>
        Task.FromResult(db.GetImage(id));

    public Task<Stream?> OpenThumbnailAsync(long imageId, CancellationToken cancellationToken = default)
    {
        var record = db.GetImage(imageId);
        if (record is null) return Task.FromResult<Stream?>(null);
        try
        {
            var cached = thumbnails.GetOrCreate(record.Path, imageId);
            Stream? stream = cached is not null && File.Exists(cached) ? File.OpenRead(cached) : null;
            return Task.FromResult(stream);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or NotSupportedException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    public async Task<GatewayPage> SearchAsync(SearchFilters filters, int pageSize = 200, string? cursor = null, CancellationToken cancellationToken = default)
    {
        var page = await IndexSearchEngine.SearchPagedAsync(db, filters, Math.Clamp(pageSize, 1, 500), GatewayCursors.Decode(cursor), cancellationToken);
        return new GatewayPage(page.Items, page.NextCursor is null ? null : GatewayCursors.Encode(page.NextCursor), page.TotalCount);
    }

    public Task<IReadOnlyList<SourceFolder>> ListSourcesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SourceFolder>>(db.GetSources());

    public Task<SourceFolder> RegisterSourceAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException($"来源目录不存在或不可读：{fullPath}");
        return Task.FromResult(db.AddSource(fullPath));
    }

    public Task<bool> RemoveSourceAsync(long sourceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(db.RemoveSource(sourceId));

    public Task<string> StartScanAsync(long sourceId, CancellationToken cancellationToken = default)
    {
        if (engine is null) throw new NotSupportedException("LocalGateway 未配置索引引擎，无法启动扫描。");
        var source = db.GetSource(sourceId) ?? throw new KeyNotFoundException($"来源不存在：{sourceId}");
        _jobs ??= new ScanJobManager();
        return Task.FromResult(_jobs.Start(engine, sourceId, source.Path));
    }

    public Task<ScanJobInfo?> GetScanJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        Task.FromResult((_jobs ?? new ScanJobManager()).Get(jobId));

    public Task<bool> CancelScanAsync(string jobId, CancellationToken cancellationToken = default) =>
        Task.FromResult((_jobs ?? new ScanJobManager()).Cancel(jobId));

    public Task<int> RemoveRecycledRecordsAsync(IReadOnlyList<long> imageIds, CancellationToken cancellationToken = default)
    {
        var removed = 0;
        foreach (var id in imageIds)
        {
            if (db.GetImage(id) is null) continue;
            db.DeleteImage(id);
            thumbnails.Delete(id);
            removed++;
        }
        return Task.FromResult(removed);
    }

    public Task<string> BackupAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        db.BackupTo(full);
        return Task.FromResult(full);
    }
}
