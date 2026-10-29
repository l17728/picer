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

    private Task<T> ReadAsync<T>(Func<PicerDatabase, T> read, CancellationToken token) =>
        Task.Run(() => db.SupportsIndependentReadSnapshots ? db.ReadSnapshot(read, token) : read(db), token);

    public Task<ImageRecord?> GetImageAsync(long id, CancellationToken cancellationToken = default) =>
        ReadAsync(reader => reader.GetImage(id), cancellationToken);

    public async Task<Stream?> OpenThumbnailAsync(long imageId, CancellationToken cancellationToken = default)
    {
        var record = await GetImageAsync(imageId, cancellationToken).ConfigureAwait(false);
        if (record is null) return null;
        return await Task.Run<Stream?>(() =>
        {
            try
            {
                var cached = thumbnails.GetOrCreate(record.Path, imageId, record.Sha256);
                return cached is not null && File.Exists(cached) ? File.OpenRead(cached) : null;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or NotSupportedException) { return null; }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Stream?> OpenFaceThumbnailAsync(long faceId, string? agentId = null, CancellationToken cancellationToken = default)
    {
        var data = await ReadAsync(reader =>
        {
            using var command = reader.Command("SELECT image_id,box_x,box_y,box_width,box_height FROM detected_faces WHERE id=$id");
            command.Parameters.AddWithValue("$id", faceId);
            using var row = command.ExecuteReader();
            if (!row.Read()) return ((string?)null, new NormalizedFaceBox(0, 0, 0, 0));
            return (reader.GetImage(row.GetInt64(0))?.Path, new NormalizedFaceBox(row.GetFloat(1), row.GetFloat(2), row.GetFloat(3), row.GetFloat(4)));
        }, cancellationToken).ConfigureAwait(false);
        if (data.Item1 is null) return null;
        return await Task.Run<Stream?>(() =>
        {
            if (!File.Exists(data.Item1)) return null;
            using var source = File.OpenRead(data.Item1);
            return FaceThumbnailCropper.Crop(source, data.Item2, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static Task<Stream?> OpenFileAsync(string? path, CancellationToken token) =>
        Task.Run<Stream?>(() => path is not null && File.Exists(path) ? File.OpenRead(path) : null, token);

    public async Task<Stream?> OpenOriginalImageAsync(long imageId, string? agentId = null, CancellationToken cancellationToken = default) =>
        await OpenFileAsync((await GetImageAsync(imageId, cancellationToken).ConfigureAwait(false))?.Path, cancellationToken).ConfigureAwait(false);

    public async Task<Stream?> OpenVideoThumbnailAsync(long videoId, string? agentId = null, CancellationToken cancellationToken = default)
    {
        var path = await ReadAsync(reader => new VideoRepository(reader).GetKeyframes(videoId, existingOnly: false).FirstOrDefault()?.Path, cancellationToken).ConfigureAwait(false);
        return await OpenFileAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Stream?> OpenVideoKeyframeAsync(long keyframeId, CancellationToken cancellationToken = default)
    {
        var path = await ReadAsync(reader =>
        {
            using var command = reader.Command("SELECT path FROM video_keyframes WHERE id=$id");
            command.Parameters.AddWithValue("$id", keyframeId);
            return command.ExecuteScalar() as string;
        }, cancellationToken).ConfigureAwait(false);
        return await OpenFileAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Stream?> OpenOriginalVideoAsync(long videoId, string? agentId = null, CancellationToken cancellationToken = default) =>
        await OpenFileAsync((await GetVideoAsync(videoId, cancellationToken).ConfigureAwait(false))?.Path, cancellationToken).ConfigureAwait(false);

    public Task<GatewayVideoPage> ListVideosAsync(long? sourceId = null, int pageSize = 200, string? cursor = null, CancellationToken cancellationToken = default) =>
        SearchVideosAsync(new(SourceId: sourceId), pageSize, cursor, cancellationToken);

    public Task<GatewayVideoPage> SearchVideosAsync(GatewayVideoQuery query, int pageSize = 200, string? cursor = null, CancellationToken cancellationToken = default) =>
        ReadAsync(reader => GatewayBrowseQueries.Videos(reader, query, pageSize, cursor, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<GatewayDirectorySummary>> ListDirectoriesAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(reader => GatewayBrowseQueries.Directories(reader, cancellationToken), cancellationToken);

    public Task<VideoRecord?> GetVideoAsync(long id, CancellationToken cancellationToken = default) =>
        ReadAsync(reader => new VideoRepository(reader).Get(id), cancellationToken);

    public async Task<GatewayPage> SearchAsync(SearchFilters filters, int pageSize = 200, string? cursor = null, CancellationToken cancellationToken = default)
    {
        var page = await IndexSearchEngine.SearchPagedAsync(db, filters, Math.Clamp(pageSize, 1, 500), GatewayCursors.Decode(cursor), cancellationToken);
        return new GatewayPage(page.Items, page.NextCursor is null ? null : GatewayCursors.Encode(page.NextCursor), page.TotalCount);
    }

    public Task<IReadOnlyList<SourceFolder>> ListSourcesAsync(CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<SourceFolder>>(reader => reader.GetSources(), cancellationToken);

    public Task<IReadOnlyList<Tag>> ListTagsAsync(CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<Tag>>(reader => SemanticEngine.GetTags(reader), cancellationToken);

    public Task<IReadOnlyList<Album>> ListAlbumsAsync(CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<Album>>(reader => SemanticEngine.GetAlbums(reader), cancellationToken);

    public Task<GatewayMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        return ReadAsync(reader =>
        {
            var albumImages = new List<GatewayAlbumImageLink>();
            using (var query = reader.Command("SELECT ai.album_id,ai.image_id FROM album_images ai JOIN images i ON i.id=ai.image_id WHERE i.status='active'"))
            using (var rows = query.ExecuteReader())
                while (rows.Read()) albumImages.Add(new(rows.GetInt64(0), rows.GetInt64(1)));
            var imageTags = new List<GatewayImageTagLink>();
            using (var query = reader.Command("SELECT image_id,tag_id FROM image_tags"))
            using (var rows = query.ExecuteReader())
                while (rows.Read()) imageTags.Add(new(rows.GetInt64(0), rows.GetInt64(1)));
            return new GatewayMetadata(albumImages, imageTags, reader.GetImageLocationHierarchyRows(),
                reader.GetAllMirroredFaces(), reader.GetAllMirroredFaceAssignments(), reader.GetAllMirroredVideoKeyframes(),
                reader.GetAllMirroredVideoTranscripts(), reader.GetAllMirroredVideoSummaries(), reader.GetAllMirroredVideoFaces(),
                reader.GetAllMirroredCloudVisionAnalyses(), reader.GetAllMirroredSceneGroupDescriptions(),
                reader.GetAllMirroredFaceReviewStates(), reader.GetAllMirroredFaceCandidateClusters(), reader.GetAllMirroredFaceCandidateMembers(),
                FaceReviewDecisions: reader.GetFaceReviewDecisions(), DescriptionVersions: reader.GetDescriptionVersions());
        }, cancellationToken);
    }

    public Task<IReadOnlyList<PersonSummary>> ListPeopleAsync(CancellationToken cancellationToken = default) =>
        ReadAsync<IReadOnlyList<PersonSummary>>(reader => new PeopleService(reader).GetAll(), cancellationToken);

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
