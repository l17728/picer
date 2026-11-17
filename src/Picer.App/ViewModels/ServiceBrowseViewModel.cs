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
    public string MediaBadge => IsVideo ? "瑙嗛" : "鍥剧墖";

    [ObservableProperty]
    private System.Windows.Media.Imaging.BitmapImage? _thumbnail;
    [ObservableProperty]
    private bool _isThumbnailLoading;
    public bool HasThumbnail => Thumbnail is not null;
}

public sealed class RemoteDirectoryNode
{
    public long SourceId { get; init; }
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
    private readonly ThumbnailWorkScheduler _cachedThumbnailGate = new(4);
    private readonly ThumbnailRetryTracker _thumbnailRetries = new();
    private sealed class ThumbnailLoad(CancellationToken token, bool visible) : IDisposable
    {
        public readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        public bool Visible = visible;
        public void Dispose() => Stop.Dispose();
    }
    private readonly Dictionary<ServicePhotoItem, ThumbnailLoad> _thumbnailLoads = [];
    private readonly HashSet<ServicePhotoItem> _deferredThumbnails = [];
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

    [ObservableProperty]
    private int _mediaFilterIndex;

    [ObservableProperty]
    private RemoteDirectoryNode? _selectedDirectory;

    public ServiceBrowseViewModel(IIndexGateway gateway, ClientServices? services = null) { _gateway = gateway; _services = services ?? new(); }

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
            if (work.Visible) _services.RemoteThumbnails.Promote(new ImageRecord { Id = item.Id, ContentSha256 = item.Sha256 });
            else
            {
                _deferredThumbnails.Add(item);
                work.Stop.Cancel();
            }
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
                if (_deferredThumbnails.Count > 0)
                {
                    var currentItems = Items.ToHashSet();
                    _deferredThumbnails.RemoveWhere(item => item.HasThumbnail || !currentItems.Contains(item));
                }
                var visibleItems = visible.Where(index => index >= 0 && index < Items.Count).Select(index => Items[index]).ToHashSet();
                var ordered = visibleItems.Concat(near).Concat(_deferredThumbnails).Distinct().ToArray();
                var now = DateTimeOffset.UtcNow;
                var pending = ordered.Where(item => !item.HasThumbnail && !item.IsThumbnailLoading
                        && _thumbnailRetries.CanRetry(item.Id, now))
                    .Take(Math.Max(0, 8 - _thumbnailLoads.Count)).ToArray();
                if (pending.Length == 0)
                {
                    if (_thumbnailLoads.Count > 0) { await Task.Delay(80); continue; }
                    var nextRetry = ordered.Where(item => !item.HasThumbnail && !item.IsThumbnailLoading)
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
                            // Re-evaluate the viewport promptly even during failure backoff.
                            await Task.Delay(delay < TimeSpan.FromMilliseconds(250) ? delay : TimeSpan.FromMilliseconds(250), pageToken);
                            continue;
                        }
                    }
                    return;
                }
                foreach (var item in pending) _ = LoadThumbnailAsync(item, _thumbnailPageCts?.Token ?? CancellationToken.None,
                    prefetch: !visibleItems.Contains(item));
                await Task.Delay(40);
            }
        }
        catch (OperationCanceledException)
        {
            // A directory switch may replace the page while the old worker is
            // sleeping in retry backoff. Its cancellation must not strand the new page.
            if (_thumbnailPageCts?.IsCancellationRequested == false && !_browseLifetime.IsCancellationRequested)
            {
                _thumbnailWorker = null;
                ScheduleThumbnailPrefetch();
            }
        }
    }

    [RelayCommand]
    private async Task PreviewAsync(ServicePhotoItem? item)
    {
        if (item is null) return;
        if (!item.IsVideo && _gateway is HttpIndexGateway remote)
            try { await Picer.App.Services.RemotePhotoPreview.OpenAsync(remote, item.MediaId, item.Path, item.Sha256); }
            catch (System.Net.Http.HttpRequestException) { await OpenOriginalAsync(item); }
        else await OpenOriginalAsync(item);
    }

    [RelayCommand]
    private async Task OpenOriginalAsync(ServicePhotoItem? item)
    {
        if (item is null || _gateway is not IMediaAssetGateway media) return;
        await Picer.App.Services.MediaPlayback.OpenAsync(media, item.MediaId, item.IsVideo, item.Path, item.Sha256);
    }

    public void CancelThumbnailWork()
    {
        _thumbnailPageCts?.Cancel();
        _browseSession?.Stop.Cancel();
        _browseLifetime.Cancel();
    }

    private async Task LoadThumbnailAsync(ServicePhotoItem item, CancellationToken cancellationToken, bool prefetch = false)
    {
        if (item.HasThumbnail || item.IsThumbnailLoading || !_thumbnailRetries.CanRetry(item.Id, DateTimeOffset.UtcNow)) return;
        item.IsThumbnailLoading = true;
        using var work = new ThumbnailLoad(cancellationToken, !prefetch);
        _thumbnailLoads[item] = work;
        using var trace = new Picer.Core.Logging.MediaReadTrace("service-gui-thumbnail", item.MediaId);
        var record = new ImageRecord { Id = item.Id, Path = item.Path, ContentSha256 = item.Sha256 };
        try
        {
            var cached = _services.RemoteThumbnails.TryGet(record);
            trace.Mark(cached is null ? "disk_miss" : "disk_hit");
            using var slot = await (cached is null ? _thumbnailGate : _cachedThumbnailGate)
                .EnterAsync(() => Volatile.Read(ref work.Visible), work.Stop.Token);
            trace.Mark("admitted");
            var image = await Picer.App.Services.ThumbnailBitmapLoader.LoadRemoteAsync(_services.RemoteThumbnails,
                record, downloadToken => item.IsVideo
                    ? _gateway is IMediaAssetGateway media
                        ? media.OpenVideoThumbnailAsync(item.MediaId, cancellationToken: downloadToken)
                        : Task.FromResult<Stream?>(null)
                    : _gateway.OpenThumbnailAsync(item.MediaId, downloadToken), work.Stop.Token,
                prefetch: !Volatile.Read(ref work.Visible));
            work.Stop.Token.ThrowIfCancellationRequested();
            if (image is null) { _thumbnailRetries.RecordFailure(item.Id, DateTimeOffset.UtcNow); return; }
            item.Thumbnail = image;
            trace.Mark("display_notified");
            _thumbnailRetries.RecordSuccess(item.Id);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            trace.Failure(exception);
            if (!cancellationToken.IsCancellationRequested)
                _thumbnailRetries.RecordFailure(item.Id, DateTimeOffset.UtcNow);
        }
        finally
        {
            trace.Mark(work.Stop.IsCancellationRequested ? "cancelled" : item.HasThumbnail ? "completed" : "empty");
            _thumbnailLoads.Remove(item);
            item.IsThumbnailLoading = false;
            if (work.Visible && work.Stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                _ = LoadThumbnailAsync(item, cancellationToken);
        }
    }
}


