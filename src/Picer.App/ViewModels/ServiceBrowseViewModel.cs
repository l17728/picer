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
    public string Path { get; init; } = "";
    public long FileSize { get; init; }
    public string? Sha256 { get; init; }

    [ObservableProperty]
    private System.Windows.Media.Imaging.BitmapImage? _thumbnail;
}

public partial class ServiceBrowseViewModel : ObservableObject
{
    private readonly IIndexGateway _gateway;
    private readonly SemaphoreSlim _thumbnailGate = new(4);
    private string? _cursor;
    private CancellationTokenSource? _loadCts;
    private readonly RemoteAssetCache _assetCache = new(Path.Combine(AppPaths.DataRoot, "remote-assets"));

    public ObservableCollection<ServicePhotoItem> Items { get; } = [];
    public ObservableCollection<SourceFolder> Sources { get; } = [];

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

    public ServiceBrowseViewModel(IIndexGateway gateway) => _gateway = gateway;

    [RelayCommand]
    public async Task SearchAsync()
    {
        if (!_sourcesLoaded)
        {
            try
            {
                Sources.Clear();
                foreach (var source in await _gateway.ListSourcesAsync()) Sources.Add(source);
                _sourcesLoaded = true;
            }
            catch (Exception exception) { StatusText = $"目录加载失败：{exception.Message}"; }
        }
        Items.Clear();
        _cursor = null;
        TotalCount = 0;
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
            var filters = new SearchFilters { IncludeMissing = false, Query = SearchText, SourceId = SelectedSource?.Id };
            var page = await _gateway.SearchAsync(filters, 200, _cursor, cts.Token);
            _cursor = page.NextCursor;
            TotalCount = page.TotalCount;
            foreach (var record in page.Items)
            {
                var item = new ServicePhotoItem { Id = record.Id, Path = record.Path, FileSize = record.FileSize, Sha256 = record.ContentSha256 ?? record.Sha256 };
                Items.Add(item);
                _ = LoadThumbnailAsync(item, cts.Token);
            }
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

    public Task SelectSourceAsync(SourceFolder? source)
    {
        SelectedSource = source;
        return SearchAsync();
    }

    [RelayCommand]
    private async Task OpenOriginalAsync(ServicePhotoItem? item)
    {
        if (item is null || _gateway is not IMediaAssetGateway media) return;
        if (item.Sha256 is { Length: 64 } cachedHash && _assetCache.TryGetCached(cachedHash) is { } cachedPath)
        {
            var extension = Path.GetExtension(item.Path);
            var openPath = string.IsNullOrWhiteSpace(extension) || string.Equals(Path.GetExtension(cachedPath), extension, StringComparison.OrdinalIgnoreCase)
                ? cachedPath
                : cachedPath + extension;
            if (!string.Equals(openPath, cachedPath, StringComparison.OrdinalIgnoreCase) && !File.Exists(openPath))
                File.Copy(cachedPath, openPath);
            Process.Start(new ProcessStartInfo(openPath) { UseShellExecute = true });
            StatusText = "鍘熷浘宸叉墦寮€锛堟湰鍦扮紦瀛樺懡涓級";
            return;
        }
        try
        {
            // The index service selects the sole online MediaAgent in the
            // single-tenant deployment.  Do not send the remote GUI machine
            // name as if it were the source agent id.
            await using var stream = await media.OpenOriginalImageAsync(item.Id);
            if (stream is null) { StatusText = "原图代理未找到文件"; return; }
            var bytes = await ReadAllAsync(stream);
            var hash = item.Sha256;
            var path = hash is { Length: 64 }
                ? await _assetCache.GetOrDownloadAsync(hash, () => new MemoryStream(bytes, writable: false))
                : await SaveTemporaryAsync(item, bytes);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            StatusText = "原图已打开（本地缓存可复用）";
        }
        catch (Exception exception) { StatusText = $"打开原图失败：{exception.Message}"; }
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    private static async Task<string> SaveTemporaryAsync(ServicePhotoItem item, byte[] bytes)
    {
        var directory = Path.Combine(AppPaths.DataRoot, "remote-assets");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"image-{item.Id}{Path.GetExtension(item.Path)}");
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    private async Task LoadThumbnailAsync(ServicePhotoItem item, CancellationToken cancellationToken)
    {
        try
        {
            await _thumbnailGate.WaitAsync(cancellationToken);
            try
            {
                await using var stream = await _gateway.OpenThumbnailAsync(item.Id, cancellationToken);
                if (stream is null) return;
                using var buffered = new MemoryStream();
                await stream.CopyToAsync(buffered, cancellationToken);
                buffered.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = buffered;
                image.EndInit();
                image.Freeze();
                Application.Current.Dispatcher.Invoke(() => item.Thumbnail = image);
            }
            finally
            {
                _thumbnailGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
    }
}
