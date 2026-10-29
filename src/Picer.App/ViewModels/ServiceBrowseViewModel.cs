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
    private readonly ThumbnailRetryTracker _thumbnailRetries = new();
    private sealed class ThumbnailLoad(CancellationToken token, bool visible) : IDisposable
    {
        public readonly CancellationTokenSource Stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        public bool Visible = visible;
        public void Dispose() => Stop.Dispose();
    }
    private readonly Dictionary<ServicePhotoItem, ThumbnailLoad> _thumbnailLoads = [];
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

    public void CancelThumbnailWork()
    {
        _thumbnailPageCts?.Cancel();
        _browseSession?.Stop.Cancel();
        _browseLifetime.Cancel();
    }

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
