using CommunityToolkit.Mvvm.Input;
using Picer.App.Infrastructure;
using Picer.Core.Gateway;
using Picer.Core.Indexing;
using Picer.Core.Logging;
using Picer.Core.Models;
using Picer.Core.Search;

namespace Picer.App.ViewModels;

public partial class ServiceBrowseViewModel
{
    private sealed class BrowseSession(SearchFilters filters, int mode, bool directoryFiltering)
    {
        public SearchFilters Filters { get; } = filters;
        public bool DirectoryFiltering { get; } = directoryFiltering;
        public CancellationTokenSource Stop { get; } = new();
        public string? PhotoCursor, VideoCursor;
        public bool PhotosDone = mode == 1, VideosDone = mode == 0;
        public int PhotoTotal = mode == 1 ? 0 : -1, VideoTotal = mode == 0 ? 0 : -1;
        public int PhotosLoaded, VideosLoaded;
        public HashSet<long> PublishedIds { get; } = [];
    }

    private readonly CancellationTokenSource _browseLifetime = new();
    private BrowseSession? _browseSession;
    private Task<IReadOnlyList<SourceFolder>>? _sourcesLoad;
    private Task? _directoryLoad;
    private bool _directoryFilteringAvailable;
    private bool? _videoSearchAvailable;

    [RelayCommand]
    public async Task SearchAsync()
    {
        _browseSession?.Stop.Cancel();
        var session = _browseSession = new BrowseSession(new SearchFilters
        {
            IncludeMissing = true, Query = SearchText, SourceId = SelectedSource?.Id,
            DirectoryPath = SelectedDirectory?.FullPath
        }, MediaFilterIndex, _directoryFilteringAvailable);
        _thumbnailPageCts?.Cancel();
        _thumbnailPageCts?.Dispose();
        _thumbnailPageCts = CancellationTokenSource.CreateLinkedTokenSource(session.Stop.Token, _browseLifetime.Token);
        Items.Clear(); TotalCount = 0; IsBusy = false;
        try
        {
            _ = LoadSourcesAndTreeAsync(session);
            await LoadMoreAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (_sourcesLoad?.IsFaulted == true) _sourcesLoad = null;
            if (IsCurrent(session)) StatusText = $"加载失败：{exception.Message}";
        }
    }

    private async Task LoadSourcesAndTreeAsync(BrowseSession session)
    {
        try
        {
            _sourcesLoad ??= _gateway.ListSourcesAsync(_browseLifetime.Token);
            var sources = await _sourcesLoad.WaitAsync(session.Stop.Token);
            if (!IsCurrent(session)) return;
            if (Sources.Count == 0) foreach (var source in sources) Sources.Add(source);
            _directoryLoad ??= BuildDirectoryTreeAsync(sources, _browseLifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (_sourcesLoad?.IsFaulted == true) _sourcesLoad = null;
            AppLogger.Warn($"Source tree load failed: type={exception.GetType().Name}");
        }
    }

    private bool IsCurrent(BrowseSession session) => ReferenceEquals(session, _browseSession) &&
        !session.Stop.IsCancellationRequested && !_browseLifetime.IsCancellationRequested;

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        var session = _browseSession;
        if (session is null || !IsCurrent(session) || IsBusy || (session.PhotosDone && session.VideosDone)) return;
        IsBusy = true;
        try
        {
            // Each branch publishes its page independently; a slow video request
            // does not delay an already-returned photo page (or vice versa).
            await Task.WhenAll(session.PhotosDone ? Task.CompletedTask : LoadPhotoPageAsync(session),
                session.VideosDone ? Task.CompletedTask : LoadVideoPageAsync(session));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (IsCurrent(session)) StatusText = $"加载失败：{exception.Message}"; }
        finally { if (IsCurrent(session)) IsBusy = false; }
    }

    private async Task LoadPhotoPageAsync(BrowseSession session)
    {
        var filters = new SearchFilters { IncludeMissing = true, Query = session.Filters.Query, SourceId = session.Filters.SourceId,
            DirectoryPath = session.DirectoryFiltering ? session.Filters.DirectoryPath : null };
        var page = await _gateway.SearchAsync(filters, 200, session.PhotoCursor, session.Stop.Token);
        if (!IsCurrent(session)) return;
        var records = session.Filters.DirectoryPath is { } directory && !session.DirectoryFiltering
            ? page.Items.Where(item => IsUnderDirectory(item.Path, directory)).ToArray() : page.Items;
        await AppendPageAsync(session, records, false);
        if (!IsCurrent(session)) return;
        session.PhotoCursor = page.NextCursor; session.PhotosDone = page.NextCursor is null;
        session.PhotosLoaded += records.Count;
        session.PhotoTotal = session.Filters.DirectoryPath is not null && !session.DirectoryFiltering
            ? session.PhotosDone ? session.PhotosLoaded : -1 : page.TotalCount;
        UpdatePageStatus(session);
    }

    private async Task LoadVideoPageAsync(BrowseSession session)
    {
        GatewayVideoPage page;
        var filtered = _videoSearchAvailable != false;
        try
        {
            page = filtered
                ? await _gateway.SearchVideosAsync(new(session.Filters.SourceId, session.Filters.DirectoryPath, session.Filters.Query), 200, session.VideoCursor, session.Stop.Token)
                : await _gateway.ListVideosAsync(session.Filters.SourceId, 200, session.VideoCursor, session.Stop.Token);
            if (filtered) _videoSearchAvailable = true;
        }
        catch (NotSupportedException)
        {
            _videoSearchAvailable = false; filtered = false;
            page = await _gateway.ListVideosAsync(session.Filters.SourceId, 200, session.VideoCursor, session.Stop.Token);
        }
        if (!IsCurrent(session)) return;
        var videos = filtered ? page.Items : page.Items.Where(video =>
            (session.Filters.DirectoryPath is null || IsUnderDirectory(video.Path, session.Filters.DirectoryPath)) &&
            (string.IsNullOrWhiteSpace(session.Filters.Query) || video.Path.Contains(session.Filters.Query, StringComparison.CurrentCultureIgnoreCase))).ToArray();
        var records = videos.Select(video => new ImageRecord
        {
            Id = -video.Id, SourceId = video.SourceId, Path = video.Path, FileSize = video.FileSize,
            ModifiedUtc = video.ModifiedUtc, TakenUtc = video.TakenUtc, Width = video.Width, Height = video.Height,
            Sha256 = video.Sha256, Status = video.Status == "active" ? ImageStatus.Active : ImageStatus.Missing
        }).ToArray();
        await AppendPageAsync(session, records, true);
        if (!IsCurrent(session)) return;
        session.VideoCursor = page.NextCursor; session.VideosDone = page.NextCursor is null;
        session.VideosLoaded += records.Length;
        session.VideoTotal = filtered ? page.TotalCount : session.VideosDone ? session.VideosLoaded : -1;
        UpdatePageStatus(session);
    }

    private async Task AppendPageAsync(BrowseSession session, IReadOnlyList<ImageRecord> records, bool videos)
    {
        await UiWork.AddInBatchesAsync(records, record =>
        {
            if (!IsCurrent(session) || !session.PublishedIds.Add(record.Id)) return;
            Items.Add(new ServicePhotoItem { Id = record.Id, MediaId = videos ? -record.Id : record.Id, IsVideo = videos,
                IsMissing = record.Status == ImageStatus.Missing, Path = record.Path, FileSize = record.FileSize,
                Sha256 = record.ContentSha256 ?? record.Sha256 });
        }, session.Stop.Token);
        if (IsCurrent(session)) ScheduleThumbnailPrefetch();
    }

    private void UpdatePageStatus(BrowseSession session)
    {
        if (!IsCurrent(session)) return;
        TotalCount = session.PhotoTotal < 0 || session.VideoTotal < 0 ? -1 : session.PhotoTotal + session.VideoTotal;
        StatusText = Items.Count == 0 && session.PhotosDone && session.VideosDone ? "无结果。"
            : TotalCount < 0 ? $"已加载 {Items.Count} 个媒体项；可继续加载后续结果。"
            : $"已加载 {Items.Count} / {TotalCount} 个媒体项（数据来自索引服务）。";
    }

    public Task SelectSourceAsync(SourceFolder? source) { SelectedSource = source; SelectedDirectory = null; return SearchAsync(); }
    public Task SelectDirectoryAsync(RemoteDirectoryNode? directory)
    {
        SelectedDirectory = directory;
        SelectedSource = directory is null ? null : Sources.FirstOrDefault(source => source.Id == directory.SourceId);
        return SearchAsync();
    }

    private async Task BuildDirectoryTreeAsync(IReadOnlyList<SourceFolder> sources, CancellationToken token)
    {
        try
        {
            IReadOnlyList<GatewayDirectorySummary> rows;
            try { rows = await _gateway.ListDirectoriesAsync(token); _directoryFilteringAvailable = true; }
            catch (NotSupportedException)
            {
                var values = new List<GatewayDirectorySummary>();
                foreach (var source in sources)
                {
                    string? cursor = null;
                    do
                    {
                        var page = await _gateway.ListImagesAsync(source.Id, false, 500, cursor, token);
                        values.AddRange(page.Items.Select(item => new GatewayDirectorySummary(source.Id, ParentPath(item.Path), 1, 0)));
                        cursor = page.NextCursor;
                    } while (cursor is not null);
                    cursor = null;
                    do
                    {
                        var page = await _gateway.ListVideosAsync(source.Id, 500, cursor, token);
                        values.AddRange(page.Items.Where(item => item.Status == "active").Select(item => new GatewayDirectorySummary(source.Id, ParentPath(item.Path), 0, 1)));
                        cursor = page.NextCursor;
                    } while (cursor is not null);
                }
                rows = values.GroupBy(item => (item.SourceId, item.Path)).Select(group =>
                    new GatewayDirectorySummary(group.Key.SourceId, group.Key.Path, group.Sum(item => item.Images), group.Sum(item => item.Videos))).ToArray();
            }
            var roots = await Task.Run(() => BuildDirectoryNodes(sources, rows), token);
            token.ThrowIfCancellationRequested();
            DirectoryTree.Clear();
            foreach (var root in roots) DirectoryTree.Add(root);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { AppLogger.Warn($"Directory summary load failed: type={exception.GetType().Name}"); }
    }

    private static RemoteDirectoryNode[] BuildDirectoryNodes(IReadOnlyList<SourceFolder> sources, IReadOnlyList<GatewayDirectorySummary> rows)
    {
        return sources.Select(source =>
        {
            var rootPath = MediaSourcePath.Normalize(source.Path);
            var entries = rows.Where(row => row.SourceId == source.Id).ToArray();
            var root = new RemoteDirectoryNode { SourceId = source.Id, Name = rootPath[(rootPath.LastIndexOf('/') + 1)..],
                FullPath = rootPath, IsSource = true, ImageCount = entries.Sum(row => row.Images + row.Videos) };
            var nodes = new Dictionary<string, RemoteDirectoryNode>(StringComparer.OrdinalIgnoreCase) { [rootPath] = root };
            foreach (var row in entries)
            {
                var directory = MediaSourcePath.Normalize(row.Path);
                if (string.Equals(directory, rootPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (!directory.StartsWith(rootPath + "/", StringComparison.OrdinalIgnoreCase))
                {
                    root.Children.Add(new RemoteDirectoryNode { SourceId = source.Id, Name = directory, FullPath = directory, ImageCount = row.Images + row.Videos });
                    continue;
                }
                var current = rootPath; var parent = root;
                foreach (var part in directory[(rootPath.Length + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    current += "/" + part;
                    if (!nodes.TryGetValue(current, out var node))
                    {
                        node = new RemoteDirectoryNode { SourceId = source.Id, Name = part, FullPath = current };
                        nodes[current] = node; parent.Children.Add(node);
                    }
                    node.ImageCount += row.Images + row.Videos; parent = node;
                }
            }
            return root;
        }).ToArray();
    }

    private static string ParentPath(string path)
    {
        var normalized = MediaSourcePath.Normalize(path); var separator = normalized.LastIndexOf('/');
        return separator < 0 ? normalized : normalized[..separator];
    }
    private static bool IsUnderDirectory(string path, string directory) =>
        MediaSourcePath.Normalize(path).StartsWith(MediaSourcePath.Normalize(directory) + "/", StringComparison.OrdinalIgnoreCase);
}
