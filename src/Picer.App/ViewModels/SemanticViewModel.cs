using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Picer.Core.Data;
using Picer.Core.Search;
using Picer.App.Infrastructure;

namespace Picer.App.ViewModels;

public partial class SemanticViewModel : ObservableObject
{
    private readonly ClientServices _services;
    [ObservableProperty]
    private ObservableCollection<TagItemViewModel> _tags = new();

    [ObservableProperty]
    private TagItemViewModel? _selectedTag;

    [ObservableProperty]
    private ObservableCollection<AlbumItemViewModel> _albums = new();

    [ObservableProperty]
    private AlbumItemViewModel? _selectedAlbum;

    [ObservableProperty]
    private string _newTagName = "";

    [ObservableProperty]
    private string _newAlbumName = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private ObservableCollection<SuggestedTagVM> _suggestions = new();

    [ObservableProperty]
    private long _currentImageId = 0;
    private IReadOnlyList<long> _selectedImageIds = [];
    public string SelectionSummary => _selectedImageIds.Count == 0
        ? "尚未选中可索引图片。请先在“目录浏览”或“搜索结果”中选择图片，再到这里添加标签、查看建议或加入相册。"
        : $"当前已选择 {_selectedImageIds.Count} 张图片；新标签会批量应用到这些图片。";

    private readonly PicerDatabase _db;
    private readonly Action? _changed;
    private CancellationTokenSource? _selectionCancellation;
    private int _initialized;

    public SemanticViewModel(PicerDatabase db, Action? changed = null, ClientServices? services = null)
    {
        _services = services ?? new();
        _db = db;
        _changed = changed;
    }

    public async Task InitializePageAsync()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
        IsBusy = true;
        StatusText = "正在加载标签与相册…";
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        try
        {
        var snapshot = await UiWork.RunDatabaseAsync(() => (Tags: SemanticEngine.GetTags(_db).ToArray(), Albums: SemanticEngine.GetAlbums(_db).ToArray()));
        Tags = new(snapshot.Tags.Select(tag => new TagItemViewModel(tag)));
        Albums = new(snapshot.Albums.Select(album => new AlbumItemViewModel(album)));
            StatusText = $"已加载 {Tags.Count} 个标签、{Albums.Count} 个相册";
        }
        catch
        {
            Interlocked.Exchange(ref _initialized, 0);
            throw;
        }
        finally { IsBusy = false; }
    }

    public void SetCurrentImage(long imageId)
    {
        CurrentImageId = imageId;
        Suggestions.Clear();
        _selectionCancellation?.Cancel();
        if (imageId <= 0) return;
        var cancellation = _selectionCancellation = new CancellationTokenSource();
        _ = LoadSuggestionsAsync(imageId, cancellation.Token);
    }

    private async Task LoadSuggestionsAsync(long imageId, CancellationToken cancellationToken)
    {
        try
        {
            var suggestions = await UiWork.RunDatabaseAsync(() =>
            {
                var local = SemanticEngine.ExtractAISuggestions(_db, imageId).ToList();
                foreach (var name in _services.CloudVisionRepository.GetSuggestedTags(imageId)
                             .Where(name => local.All(existing => !string.Equals(existing.TagName, name, StringComparison.OrdinalIgnoreCase))))
                    local.Add(new SuggestedTag(name, Confidence.Medium));
                return local;
            }, cancellationToken);
            if (cancellationToken.IsCancellationRequested || CurrentImageId != imageId) return;
            foreach (var s in suggestions)
            {
                var item = new SuggestedTagVM(s);
                item.OnAccept = AcceptSuggestionAsync;
                item.OnReject = RejectSuggestionAsync;
                Suggestions.Add(item);
                await Task.Yield();
            }
        }
        catch (OperationCanceledException) { }
    }

    public void SetSelection(IEnumerable<long> imageIds)
    {
        _selectedImageIds = imageIds.Where(id => id > 0).Distinct().ToArray();
        OnPropertyChanged(nameof(SelectionSummary));
        SetCurrentImage(_selectedImageIds.FirstOrDefault());
    }

    public void LoadTags()
    {
        Tags.Clear();
        foreach (var tag in SemanticEngine.GetTags(_db))
            Tags.Add(new TagItemViewModel(tag));
    }

    public void LoadAlbums()
    {
        Albums.Clear();
        foreach (var album in SemanticEngine.GetAlbums(_db))
            Albums.Add(new AlbumItemViewModel(album));
    }

    [RelayCommand]
    private async Task AddTagAsync()
    {
        if (string.IsNullOrWhiteSpace(NewTagName))
            return;

        try
        {
            var name = NewTagName;
            var imageIds = _selectedImageIds.ToArray();
            var tag = await UiWork.RunDatabaseAsync(() =>
            {
                var created = SemanticEngine.AddTag(_db, name);
                foreach (var imageId in imageIds) SemanticEngine.AssignTag(_db, imageId, created.Id);
                return created;
            });

            Tags.Add(new TagItemViewModel(tag));
            NewTagName = "";
            StatusText = $"已为 {_selectedImageIds.Count} 张图片添加用户标签：{tag.Name}";
            _changed?.Invoke();
        }
        catch (Exception ex) when (ex.Message.Contains("UNIQUE"))
        {
            StatusText = "标签已存在";
        }
    }

    [RelayCommand]
    private async Task AddAlbumAsync()
    {
        if (string.IsNullOrWhiteSpace(NewAlbumName))
        {
            StatusText = "请输入相册名称";
            return;
        }
        var album = await UiWork.RunDatabaseAsync(() => SemanticEngine.AddAlbum(_db, NewAlbumName));
        var item = new AlbumItemViewModel(album);
        Albums.Add(item);
        SelectedAlbum = item;
        NewAlbumName = "";
        StatusText = $"已创建相册：{album.Name}";
        _changed?.Invoke();
    }

    [RelayCommand]
    private async Task DeleteSelectedTagAsync()
    {
        if (SelectedTag is null)
            return;

        var result = MessageBox.Show($"确定删除标签 '{SelectedTag.Tag.Name}' 吗？", 
            "删除标签", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            return;

        var selected = SelectedTag;
        await UiWork.RunDatabaseAsync(() => SemanticEngine.DeleteTag(_db, selected.Tag.Id));
        Tags.Remove(selected);
        StatusText = "标签已删除";
        _changed?.Invoke();
    }

    [RelayCommand]
    private async Task AddToAlbumAsync()
    {
        if (SelectedAlbum is null || CurrentImageId <= 0)
        {
            StatusText = "请先选择相册和图片";
            return;
        }

        await UiWork.RunDatabaseAsync(() => SemanticEngine.AddToAlbum(_db, SelectedAlbum.Album.Id, CurrentImageId));
        StatusText = $"已将图片添加到'{SelectedAlbum.Album.Name}'";
        _changed?.Invoke();
    }

    [RelayCommand]
    private async Task RemoveFromAlbumAsync()
    {
        if (SelectedAlbum is null || CurrentImageId <= 0)
        {
            StatusText = "请先选择相册和图片";
            return;
        }

        await UiWork.RunDatabaseAsync(() => SemanticEngine.RemoveFromAlbum(_db, SelectedAlbum.Album.Id, CurrentImageId));
        StatusText = $"已从'{SelectedAlbum.Album.Name}'移除图片";
        _changed?.Invoke();
    }

    [RelayCommand]
    private async Task AutoCreateTimeLineAlbumsAsync()
    {
        IsBusy = true;
        try
        {
            var albums = await UiWork.RunDatabaseAsync(() => SemanticEngine.AutoCreateTimeLineAlbums(_db));
            var refreshed = await UiWork.RunDatabaseAsync(() => SemanticEngine.GetAlbums(_db).ToArray());
            Albums = new(refreshed.Select(album => new AlbumItemViewModel(album)));
            StatusText = $"时间线相册已增量更新；新建 {albums.Count} 个，其余仅补充新增照片";
            _changed?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AutoCreateCameraAlbumsAsync()
    {
        IsBusy = true;
        try
        {
            var albums = await UiWork.RunDatabaseAsync(() => SemanticEngine.AutoCreateCameraAlbums(_db, autoTag: true));
            var snapshot = await UiWork.RunDatabaseAsync(() => (Albums: SemanticEngine.GetAlbums(_db).ToArray(), Tags: SemanticEngine.GetTags(_db).ToArray()));
            Albums = new(snapshot.Albums.Select(album => new AlbumItemViewModel(album)));
            Tags = new(snapshot.Tags.Select(tag => new TagItemViewModel(tag)));
            StatusText = $"相机相册已增量更新；新建 {albums.Count} 个，其余仅补充新增照片和标签";
            _changed?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AcceptSuggestionAsync(string tagName)
    {
        var imageId = CurrentImageId;
        var tag = await UiWork.RunDatabaseAsync(() =>
        {
            var existing = SemanticEngine.GetTags(_db).FirstOrDefault(t => t.Name == tagName);
            if (existing is null && imageId > 0) existing = SemanticEngine.AddTag(_db, tagName);
            if (existing is not null && imageId > 0) SemanticEngine.AssignTag(_db, imageId, existing.Id);
            return existing;
        });
        if (tag is not null && Tags.All(item => item.Tag.Id != tag.Id)) Tags.Add(new TagItemViewModel(tag));

        StatusText = $"已接受建议并标记: {tagName}";
        Suggestions.Remove(Suggestions.First(item => item.TagName == tagName));
        _changed?.Invoke();
    }

    private async Task RejectSuggestionAsync(string tagName)
    {
        if (CurrentImageId <= 0) return;
        await UiWork.RunDatabaseAsync(() => SemanticEngine.RejectSuggestion(_db, CurrentImageId, tagName));
        var item = Suggestions.FirstOrDefault(suggestion => suggestion.TagName == tagName);
        if (item is not null) Suggestions.Remove(item);
        StatusText = $"已拒绝建议：{tagName}，不会再次自动出现";
    }
}

// Supporting ViewModel classes
public partial class TagItemViewModel : ObservableObject
{
    public Tag Tag { get; }
    
    [ObservableProperty]
    private int _albumCount; // Images count

    public TagItemViewModel(Tag tag)
    {
        Tag = tag;
    }
}

public partial class AlbumItemViewModel : ObservableObject
{
    public Album Album { get; }
    
    [ObservableProperty]
    private int _imageCount;

    public AlbumItemViewModel(Album album)
    {
        Album = album;
    }
    
    public void UpdateImageCount(int count)
    {
        ImageCount = count;
    }
}

public partial class SuggestedTagVM : ObservableObject
{
    public SuggestedTag SuggestedTag { get; }
    
    public string TagName => SuggestedTag.TagName;
    public Confidence Confidence => SuggestedTag.Confidence;
    public string ConfidenceLabel => Confidence switch
    {
        Confidence.High => "高置信度",
        Confidence.Medium => "中等",
        _ => "低置信度"
    };

    [RelayCommand]
    private async Task AcceptAsync()
    {
        if (OnAccept != null && !string.IsNullOrEmpty(TagName))
            await OnAccept(TagName);
    }

    public Func<string, Task>? OnAccept { get; set; }
    public Func<string, Task>? OnReject { get; set; }

    [RelayCommand]
    private async Task RejectAsync()
    {
        if (OnReject is not null) await OnReject(TagName);
    }
    
    public SuggestedTagVM(SuggestedTag suggestedTag)
    {
        SuggestedTag = suggestedTag;
    }
}
