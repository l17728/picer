using Picer.Core.Data;
using Picer.Core.Indexing;
using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Search;
using Picer.Core.Thumbnails;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.Core.Gateway;

public sealed class LocalGateway(PicerDatabase db, ThumbnailService thumbnails, IndexEngine? engine = null) : IIndexGateway, IMediaAssetGateway
{
    private ScanJobManager? _jobs;
    private readonly VideoRepository _videos = new(db);

    public void Dispose() { }
    public Task<GatewayChangePage> GetChangesAsync(GatewayChangeRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var independent = db.OpenIndependentConnection();
        return Task.FromResult(independent.ReadMirrorChanges(request));
    }
    public async Task<bool> PurgeVideoAsync(long id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var purger = new VideoRecordPurger(db); var snapshot = purger.Inspect(id);
        if (snapshot is null) return true;
        using var removal = await VideoOperationLifetime.BeginPurgeAsync(snapshot.Path, cancellationToken);
        return purger.Purge(id);
    }
    public Task<bool> PurgeMissingImageAsync(long id, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(new ImageRecordPurger(db).Purge(id)); }
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
            var cached = thumbnails.GetOrCreate(record.Path, imageId, record.Sha256);
            Stream? stream = cached is not null && File.Exists(cached) ? File.OpenRead(cached) : null;
            return Task.FromResult(stream);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or NotSupportedException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    public Task<Stream?> OpenFaceThumbnailAsync(long faceId, string? agentId = null, CancellationToken cancellationToken = default)
    {
        using var command = db.Command("SELECT image_id,box_x,box_y,box_width,box_height FROM detected_faces WHERE id=$id");
        command.Parameters.AddWithValue("$id", faceId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return Task.FromResult<Stream?>(null);
        var image = db.GetImage(reader.GetInt64(0));
        if (image is null || !File.Exists(image.Path)) return Task.FromResult<Stream?>(null);
        using var source = File.OpenRead(image.Path);
        var crop = FaceThumbnailCropper.Crop(source, new NormalizedFaceBox((float)reader.GetDouble(1), (float)reader.GetDouble(2), (float)reader.GetDouble(3), (float)reader.GetDouble(4)), cancellationToken);
        return Task.FromResult(crop);
    }

    public Task<Stream?> OpenOriginalImageAsync(long imageId, string? agentId = null, CancellationToken cancellationToken = default)
    {
        var path = db.GetImage(imageId)?.Path;
        return Task.FromResult<Stream?>(path is not null && File.Exists(path) ? File.OpenRead(path) : null);
    }

    public Task<Stream?> OpenVideoThumbnailAsync(long videoId, string? agentId = null, CancellationToken cancellationToken = default)
    {
        var path = _videos.GetKeyframes(videoId).FirstOrDefault()?.Path;
        return Task.FromResult<Stream?>(path is not null && File.Exists(path) ? File.OpenRead(path) : null);
    }

    public Task<Stream?> OpenVideoKeyframeAsync(long keyframeId, CancellationToken cancellationToken = default)
    {
        using var command = db.Command("SELECT path FROM video_keyframes WHERE id=$id");
        command.Parameters.AddWithValue("$id", keyframeId);
        var path = command.ExecuteScalar() as string;
        return Task.FromResult<Stream?>(path is not null && File.Exists(path) ? File.OpenRead(path) : null);
    }

    public Task<Stream?> OpenOriginalVideoAsync(long videoId, string? agentId = null, CancellationToken cancellationToken = default)
    {
        var path = _videos.GetAll().FirstOrDefault(video => video.Id == videoId)?.Path;
        return Task.FromResult<Stream?>(path is not null && File.Exists(path) ? File.OpenRead(path) : null);
    }

    public Task<GatewayVideoPage> ListVideosAsync(long? sourceId = null, int pageSize = 200, string? cursor = null, CancellationToken cancellationToken = default)
    {
        var all = _videos.GetAll().Where(video => sourceId is null || video.SourceId == sourceId).ToArray();
        var offset = int.TryParse(cursor, out var parsed) ? Math.Max(0, parsed) : 0;
        var size = Math.Clamp(pageSize, 1, 500);
        var items = all.Skip(offset).Take(size).ToArray();
        var next = offset + items.Length < all.Length ? (offset + items.Length).ToString() : null;
        return Task.FromResult(new GatewayVideoPage(items, next, all.Length));
    }

    public Task<VideoRecord?> GetVideoAsync(long id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_videos.GetAll().FirstOrDefault(video => video.Id == id));

    public async Task<GatewayPage> SearchAsync(SearchFilters filters, int pageSize = 200, string? cursor = null, CancellationToken cancellationToken = default)
    {
        var page = await IndexSearchEngine.SearchPagedAsync(db, filters, Math.Clamp(pageSize, 1, 500), GatewayCursors.Decode(cursor), cancellationToken);
        return new GatewayPage(page.Items, page.NextCursor is null ? null : GatewayCursors.Encode(page.NextCursor), page.TotalCount);
    }

    public Task<IReadOnlyList<SourceFolder>> ListSourcesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SourceFolder>>(db.GetSources());

    public Task<IReadOnlyList<Tag>> ListTagsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Tag>>(SemanticEngine.GetTags(db));

    public Task<IReadOnlyList<Album>> ListAlbumsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Album>>(SemanticEngine.GetAlbums(db));

    public Task<GatewayMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        var albumImages = SemanticEngine.GetAlbums(db).SelectMany(album =>
            SemanticEngine.GetAlbumImages(db, album.Id).Select(image => new GatewayAlbumImageLink(album.Id, image.Id))).ToArray();
        var imageTags = db.GetImages(includeMissing: true).SelectMany(image =>
            SemanticEngine.GetImageTags(db, image.Id).Select(tag => new GatewayImageTagLink(image.Id, tag.Id))).ToArray();
        return Task.FromResult(new GatewayMetadata(albumImages, imageTags, db.GetImageLocationHierarchyRows(),
            db.GetAllMirroredFaces(), db.GetAllMirroredFaceAssignments(), db.GetAllMirroredVideoKeyframes(),
            db.GetAllMirroredVideoTranscripts(), db.GetAllMirroredVideoSummaries(), db.GetAllMirroredVideoFaces(),
            db.GetAllMirroredCloudVisionAnalyses(), db.GetAllMirroredSceneGroupDescriptions(),
            db.GetAllMirroredFaceReviewStates(), db.GetAllMirroredFaceCandidateClusters(), db.GetAllMirroredFaceCandidateMembers(),
            FaceReviewDecisions: db.GetFaceReviewDecisions()));
    }

    public Task<IReadOnlyList<PersonSummary>> ListPeopleAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PersonSummary>>(new PeopleService(db).GetAll());

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
