using System.Collections.ObjectModel;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Picer.Core.Gateway;
using Picer.Core.Models;
using Picer.Core.Search;

namespace Picer.App.ViewModels;

public partial class ServicePhotoItem : ObservableObject
{
    public long Id { get; init; }
    public long MediaId { get; init; }
    public bool IsVideo { get; init; }
    public bool IsMissing { get; init; }
    public string Path { get; init; } = "";
    public long FileSize { get; init; }
    public string? Sha256 { get; init; }
    public string MediaBadge => IsVideo ? "视频" : "图片";

    [ObservableProperty]
    private System.Windows.Media.Imaging.BitmapImage? _thumbnail;
    [ObservableProperty]
    private bool _isThumbnailLoading;
    public bool HasThumbnail => Thumbnail is not null;
}

public sealed class RemoteDirectoryNode
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public int ImageCount { get; set; }
    public bool IsSource { get; init; }
    public ObservableCollection<RemoteDirectoryNode> Children { get; } = [];
}

public partial class ServiceBrowseViewModel : ObservableObject
{
    private readonly ClientServices _services;
    private readonly IIndexGateway _gateway;
    // The authenticated service also serves mirror/index traffic. Keep visible
    // thumbnail requests below its per-device admission limit.
    private readonly ThumbnailWorkScheduler _thumbnailGate = new(2);
    private readonly ThumbnailRetryTracker _thumbnailRetries = new();
    private sealed class ThumbnailLoad(CancellationToken token, bool visible) : IDisposable
    {
        public readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        public bool Visible = visible;
        public void Dispose() => Stop.Dispose();
    }
    private readonly Dictionary<ServicePhotoItem, ThumbnailLoad> _thumbnailLoads = [];
    private string? _cursor;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _thumbnailPageCts = new();
    private Task? _thumbnailWorker;
    private long _thumbnailRequestTicks;
    private HashSet<int> _visibleThumbnailIndices = [];

    public ObservableCollection<ServicePhotoItem> Items { get; } = [];
    public ObservableCollection<SourceFolder> Sources { get; } = [];
    public ObservableCollection<RemoteDirectoryNode> DirectoryTree { get; } = [];

    [ObservableProperty]
    private SourceFolder? _selectedSource;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private bool _isBusy;
    private bool _sourcesLoaded;

    [ObservableProperty]
    private int _mediaFilterIndex;

    [ObservableProperty]
    private RemoteDirectoryNode? _selectedDirectory;

    public ServiceBrowseViewModel(IIndexGateway gateway, ClientServices? services = null) { _gateway = gateway; _services = services ?? new(); }

    [RelayCommand]
    public async Task SearchAsync()
    {
        if (!_sourcesLoaded)
        {
            try
            {
                Sources.Clear();
                foreach (var source in await _gateway.ListSourcesAsync()) Sources.Add(source);
                await BuildDirectoryTreeAsync();
                _sourcesLoaded = true;
            }
            catch (Exception exception) { StatusText = $"目录加载失败：{exception.Message}"; }
        }
        Items.Clear();
        _cursor = null;
        TotalCount = 0;
        _thumbnailPageCts?.Cancel();
        _thumbnailPageCts?.Dispose();
        _thumbnailPageCts = new CancellationTokenSource();
        await LoadMoreAsync();
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        try
        {
            var filters = new SearchFilters { IncludeMissing = true, Query = SearchText, SourceId = SelectedSource?.Id };
            var page = MediaFilterIndex == 1
                ? await SearchVideosAsync(filters, cts.Token)
                : MediaFilterIndex == 2
                    ? await SearchAllAsync(filters, cts.Token)
                    : SelectedDirectory is null
                        ? await _gateway.SearchAsync(filters, 200, _cursor, cts.Token)
                        : await SearchDirectoryAsync(filters, SelectedDirectory.FullPath, cts.Token);
            _cursor = page.NextCursor;
            TotalCount = page.TotalCount;
            foreach (var record in page.Items)
            {
                var isVideo = record.Id < 0;
                var mediaId = isVideo ? -record.Id : record.Id;
                var item = new ServicePhotoItem { Id = record.Id, MediaId = mediaId, IsVideo = isVideo, IsMissing = record.Status == ImageStatus.Missing, Path = record.Path, FileSize = record.FileSize, Sha256 = record.ContentSha256 ?? record.Sha256 };
                Items.Add(item);
            }
            ScheduleThumbnailPrefetch();
            StatusText = Items.Count == 0
                ? "无结果。"
                : $"已加载 {Items.Count} / {TotalCount} 张（数据来自索引服务）。";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText = $"加载失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task SetMediaFilterAsync(int index)
    {
        MediaFilterIndex = Math.Clamp(index, 0, 2);
        return SearchAsync();
    }

    public void ScheduleThumbnailPrefetch(IEnumerable<int>? visibleIndices = null)
    {
        if (visibleIndices is not null) _visibleThumbnailIndices = visibleIndices.ToHashSet();
        var visible = _visibleThumbnailIndices.Where(index => index >= 0 && index < Items.Count).Select(index => Items[index]).ToHashSet();
        foreach (var (item, work) in _thumbnailLoads.ToArray())
        {
            Volatile.Write(ref work.Visible, visible.Contains(item));
            if (work.Visible) _services.RemoteThumbnails.Promote(new ImageRecord { Id = item.MediaId, ContentSha256 = item.Sha256 });
            else work.Stop.Cancel();
        }
        _thumbnailGate.RefreshPriorities();
        foreach (var item in visible) _ = LoadThumbnailAsync(item, _thumbnailPageCts?.Token ?? CancellationToken.None);
        Volatile.Write(ref _thumbnailRequestTicks, DateTime.UtcNow.Ticks);
        if (_thumbnailWorker is null || _thumbnailWorker.IsCompleted)
            _thumbnailWorker = PrefetchThumbnailsWhenIdleAsync();
    }

    private async Task PrefetchThumbnailsWhenIdleAsync()
    {
        try
        {
            await Task.Delay(750);
            while (_thumbnailPageCts?.IsCancellationRequested != true)
            {
                var remaining = Volatile.Read(ref _thumbnailRequestTicks) + TimeSpan.TicksPerMillisecond * 750 - DateTime.UtcNow.Ticks;
                if (remaining > 0) { await Task.Delay(TimeSpan.FromTicks(remaining)); continue; }
                var visible = _visibleThumbnailIndices;
                var first = visible.Count > 0 ? visible.Min() : 0;
                var span = Math.Clamp(visible.Count, 8, 128);
                var near = Items.Skip(Math.Max(0, first - span)).Take(span * 3);
                var ordered = visible.Select(index => index >= 0 && index < Items.Count ? Items[index] : null)
                    .Concat(near.Where(item => !visible.Contains(Items.IndexOf(item))))
                    .OfType<ServicePhotoItem>().ToArray();
                var now = DateTimeOffset.UtcNow;
                var pending = ordered.Where(item => !item.IsVideo && !item.HasThumbnail && !item.IsThumbnailLoading
                        && _thumbnailRetries.CanRetry(item.Id, now))
                    .Take(Math.Max(0, 8 - _thumbnailLoads.Count)).ToArray();
                if (pending.Length == 0)
                {
                    if (_thumbnailLoads.Count > 0) { await Task.Delay(80); continue; }
                    var nextRetry = ordered.Where(item => !item.IsVideo && !item.HasThumbnail && !item.IsThumbnailLoading)
                        .Select(item => _thumbnailRetries.GetRetryAt(item.Id))
                        .Where(retryAt => retryAt is not null)
                        .Select(retryAt => retryAt!.Value)
                        .DefaultIfEmpty(DateTimeOffset.MaxValue)
                        .Min();
                    if (nextRetry != DateTimeOffset.MaxValue)
                    {
                        var delay = nextRetry - DateTimeOffset.UtcNow;
                        if (delay > TimeSpan.Zero)
                        {
                            var pageToken = _thumbnailPageCts?.Token ?? CancellationToken.None;
                            await Task.Delay(delay, pageToken);
                            continue;
                        }
                    }
                    return;
                }
                foreach (var item in pending) _ = LoadThumbnailAsync(item, _thumbnailPageCts?.Token ?? CancellationToken.None,
                    prefetch: !visible.Contains(Items.IndexOf(item)));
                await Task.Delay(40);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<GatewayPage> SearchVideosAsync(SearchFilters filters, CancellationToken cancellationToken)
    {
        var videos = new List<Picer.Core.Video.VideoRecord>();
        string? cursor = null;
        do
        {
            var page = await _gateway.ListVideosAsync(filters.SourceId, 500, cursor, cancellationToken);
            videos.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        var directory = SelectedDirectory?.FullPath;
        var selected = videos.Where(video => directory is null || IsUnderDirectory(video.Path, directory))
            .Where(video => string.IsNullOrWhiteSpace(filters.Query) || video.Path.Contains(filters.Query, StringComparison.CurrentCultureIgnoreCase))
            .Select(video => new ImageRecord { Id = -video.Id, SourceId = video.SourceId, Path = video.Path, FileSize = video.FileSize, ModifiedUtc = video.ModifiedUtc, TakenUtc = video.TakenUtc, Width = video.Width, Height = video.Height })
            .ToArray();
        return new GatewayPage(selected, null, selected.Length);
    }

    private async Task<GatewayPage> SearchAllAsync(SearchFilters filters, CancellationToken cancellationToken)
    {
        var images = SelectedDirectory is null
            ? await _gateway.SearchAsync(filters, 200, null, cancellationToken)
            : await SearchDirectoryAsync(filters, SelectedDirectory.FullPath, cancellationToken);
        var videos = await SearchVideosAsync(filters, cancellationToken);
        return new GatewayPage(images.Items.Concat(videos.Items).ToArray(), null, images.TotalCount + videos.TotalCount);
    }

    public Task SelectSourceAsync(SourceFolder? source)
    {
        SelectedSource = source;
        return SearchAsync();
    }

    public async Task SelectDirectoryAsync(RemoteDirectoryNode? directory)
    {
        SelectedDirectory = directory;
        SelectedSource = directory is null
            ? null
            : Sources.FirstOrDefault(source => string.Equals(
                Path.GetFullPath(directory.FullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(source.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase) ||
                directory.FullPath.StartsWith(Path.GetFullPath(source.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        await SearchAsync();
    }

    private async Task BuildDirectoryTreeAsync()
    {
        DirectoryTree.Clear();
        foreach (var source in Sources)
        {
            var rootPath = Path.GetFullPath(source.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var all = new List<ImageRecord>();
            string? cursor = null;
            do
            {
                var page = await _gateway.ListImagesAsync(source.Id, false, 500, cursor);
                all.AddRange(page.Items);
                cursor = page.NextCursor;
            } while (cursor is not null);

            var root = new RemoteDirectoryNode { Name = Path.GetFileName(rootPath), FullPath = rootPath, ImageCount = all.Count, IsSource = true };
            var nodes = new Dictionary<string, RemoteDirectoryNode>(StringComparer.OrdinalIgnoreCase) { [rootPath] = root };
            foreach (var record in all)
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(record.Path));
                if (string.IsNullOrWhiteSpace(directory) || !directory.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) continue;
                var relative = Path.GetRelativePath(rootPath, directory);
                if (relative == ".") continue;
                var current = rootPath;
                RemoteDirectoryNode? leaf = null;
                foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, part);
                    if (!nodes.TryGetValue(current, out var node))
                    {
                        node = new RemoteDirectoryNode { Name = part, FullPath = current, ImageCount = 0 };
                        nodes[current] = node;
                        var parentPath = Path.GetDirectoryName(current) ?? rootPath;
                        nodes[parentPath].Children.Add(node);
                    }
                    leaf = node;
                }
                if (leaf is not null) leaf.ImageCount++;
            }
            DirectoryTree.Add(root);
        }
    }

    private async Task<GatewayPage> SearchDirectoryAsync(SearchFilters filters, string directory, CancellationToken cancellationToken)
    {
        var items = new List<ImageRecord>();
        string? cursor = null;
        do
        {
            var page = await _gateway.SearchAsync(filters, 500, cursor, cancellationToken);
            items.AddRange(page.Items.Where(record => IsUnderDirectory(record.Path, directory)));
            cursor = page.NextCursor;
        } while (cursor is not null);
        if (!string.IsNullOrWhiteSpace(filters.Query))
            items = items.Where(record => record.Path.Contains(filters.Query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        return new GatewayPage(items, null, items.Count);
    }

    private static bool IsUnderDirectory(string path, string directory)
    {
        var normalizedPath = Path.GetFullPath(path);
        var normalizedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = normalizedDirectory + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private async Task PreviewAsync(ServicePhotoItem? item)
    {
        if (item is null) return;
        if (!item.IsVideo && _gateway is HttpIndexGateway remote)
            await Picer.App.Services.RemotePhotoPreview.OpenAsync(remote, item.MediaId, item.Path, item.Sha256);
        else await OpenOriginalAsync(item);
    }

    [RelayCommand]
    private async Task OpenOriginalAsync(ServicePhotoItem? item)
    {
        if (item is null || _gateway is not IMediaAssetGateway media) return;
        await Picer.App.Services.MediaPlayback.OpenAsync(media, item.MediaId, item.IsVideo, item.Path, item.Sha256);
    }

    public void CancelThumbnailWork() => _thumbnailPageCts?.Cancel();

    private async Task LoadThumbnailAsync(ServicePhotoItem item, CancellationToken cancellationToken, bool prefetch = false)
    {
        if (item.IsVideo || item.HasThumbnail || item.IsThumbnailLoading || !_thumbnailRetries.CanRetry(item.Id, DateTimeOffset.UtcNow)) return;
        item.IsThumbnailLoading = true;
        using var work = new ThumbnailLoad(cancellationToken, !prefetch);
        _thumbnailLoads[item] = work;
        try
        {
            using var slot = await _thumbnailGate.EnterAsync(() => Volatile.Read(ref work.Visible), work.Stop.Token);
                var record = new ImageRecord { Id = item.MediaId, Path = item.Path, ContentSha256 = item.Sha256 };
                var path = _services.RemoteThumbnails.TryGet(record) ?? await _services.RemoteThumbnails.GetOrDownloadAsync(
                    record, downloadToken => _gateway.OpenThumbnailAsync(item.Id, downloadToken), work.Stop.Token,
                    prefetch: !Volatile.Read(ref work.Visible));
                work.Stop.Token.ThrowIfCancellationRequested();
                if (path is null || !File.Exists(path))
                {
                    _thumbnailRetries.RecordFailure(item.Id, DateTimeOffset.UtcNow);
                    return;
                }
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 256;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                Application.Current.Dispatcher.Invoke(() => item.Thumbnail = image);
                _thumbnailRetries.RecordSuccess(item.Id);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            if (!cancellationToken.IsCancellationRequested)
                _thumbnailRetries.RecordFailure(item.Id, DateTimeOffset.UtcNow);
        }
        finally
        {
            _thumbnailLoads.Remove(item);
            item.IsThumbnailLoading = false;
            if (work.Visible && work.Stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                _ = LoadThumbnailAsync(item, cancellationToken);
        }
    }
}
