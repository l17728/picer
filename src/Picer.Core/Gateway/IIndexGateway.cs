using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Search;
using Picer.Core.Video;

namespace Picer.Core.Gateway;

public sealed record GatewayHealth(string Status, int SchemaVersion, string MinClientVersion, string ServiceVersion);

public sealed record GatewayPage(IReadOnlyList<ImageRecord> Items, string? NextCursor, int TotalCount);
public sealed record GatewayVideoPage(IReadOnlyList<VideoRecord> Items, string? NextCursor, int TotalCount);

public interface IIndexGateway : IDisposable
{
    Task<GatewayHealth> GetHealthAsync(CancellationToken cancellationToken = default);
    Task<GatewayPage> ListImagesAsync(long? sourceId = null, bool includeMissing = false, int pageSize = 200, string? cursor = null, CancellationToken cancellationToken = default);
    Task<ImageRecord?> GetImageAsync(long id, CancellationToken cancellationToken = default);
    Task<Stream?> OpenThumbnailAsync(long imageId, CancellationToken cancellationToken = default);
    Task<GatewayVideoPage> ListVideosAsync(long? sourceId = null, int pageSize = 200, string? cursor = null, CancellationToken cancellationToken = default);
    Task<VideoRecord?> GetVideoAsync(long id, CancellationToken cancellationToken = default);
    Task<GatewayPage> SearchAsync(SearchFilters filters, int pageSize = 200, string? cursor = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SourceFolder>> ListSourcesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Tag>> ListTagsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Album>> ListAlbumsAsync(CancellationToken cancellationToken = default);
    Task<GatewayMetadata> GetMetadataAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PersonSummary>> ListPeopleAsync(CancellationToken cancellationToken = default);
    Task<SourceFolder> RegisterSourceAsync(string path, CancellationToken cancellationToken = default);
    Task<bool> RemoveSourceAsync(long sourceId, CancellationToken cancellationToken = default);
    Task<string> StartScanAsync(long sourceId, CancellationToken cancellationToken = default);
    Task<ScanJobInfo?> GetScanJobAsync(string jobId, CancellationToken cancellationToken = default);
    Task<bool> CancelScanAsync(string jobId, CancellationToken cancellationToken = default);
    Task<int> RemoveRecycledRecordsAsync(IReadOnlyList<long> imageIds, CancellationToken cancellationToken = default);
    Task<string> BackupAsync(string destinationPath, CancellationToken cancellationToken = default);
}
