using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Picer.Core.Gateway;
using Picer.Core.Search;

namespace Picer.App.ViewModels;

public partial class ServicePhotoItem : ObservableObject
{
    public long Id { get; init; }
    public string Path { get; init; } = "";
    public long FileSize { get; init; }

    [ObservableProperty]
    private System.Windows.Media.Imaging.BitmapImage? _thumbnail;
}

public partial class ServiceBrowseViewModel : ObservableObject
{
    private readonly IIndexGateway _gateway;
    private readonly SemaphoreSlim _thumbnailGate = new(4);
    private string? _cursor;
    private CancellationTokenSource? _loadCts;

    public ObservableCollection<ServicePhotoItem> Items { get; } = [];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private bool _isBusy;

    public ServiceBrowseViewModel(IIndexGateway gateway) => _gateway = gateway;

    [RelayCommand]
    public async Task SearchAsync()
    {
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
            var filters = new SearchFilters { IncludeMissing = false, Query = SearchText };
            var page = await _gateway.SearchAsync(filters, 200, _cursor, cts.Token);
            _cursor = page.NextCursor;
            TotalCount = page.TotalCount;
            foreach (var record in page.Items)
            {
                var item = new ServicePhotoItem { Id = record.Id, Path = record.Path, FileSize = record.FileSize };
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
