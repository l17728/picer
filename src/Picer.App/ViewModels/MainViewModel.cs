using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Picer.Core;
using Picer.Core.Data;
using Picer.Core.Diagnostics;
using Picer.Core.Indexing;
using Picer.Core.Geocoding;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Vision;
using Picer.App.Infrastructure;
using Picer.Core.FileOperations;
using Picer.Core.Hashing;
using Picer.App.Views;

namespace Picer.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly PicerDatabase _db;
    private IndexEngine _engine;
    private readonly IReverseGeocoder _geocoder;
    private CancellationTokenSource? _indexCts;
    private readonly DispatcherTimer _searchDebounce;
    private readonly StringBuilder _logBuffer = new();
    private const int MaxLogLines = 2000;
    private SearchCursor? _searchCursor;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _directoryLoadCts;
    private CancellationTokenSource? _cloudCts;
    private CancellationTokenSource? _photoContextCts;
    private readonly CancellationTokenSource _locationEnrichmentCts = new();
    private int _locationEnrichmentRunning;
    private long _searchVersion;
    private string? _resolvedGeoQuery;
    private readonly DirectoryClassificationPreferences _directoryClassification = new(AppPaths.DirectoryClassificationSettingsPath);
    private GeoBounds? _resolvedGeoBounds;

    public ObservableCollection<SourceNodeViewModel> Sources { get; } = new();
    public ObservableCollection<ImageItemViewModel> Images { get; } = new();
    public ObservableCollection<TagItemViewModel> Tags { get; } = new();
    public ObservableCollection<AlbumItemViewModel> Albums { get; } = new();
    public ObservableCollection<DirectoryNodeViewModel> DirectoryTree { get; } = new();
    public ObservableCollection<ImageItemViewModel> DirectoryImages { get; } = new();
    public ObservableCollection<ImageItemViewModel> SelectedImages { get; } = new();
    public SemanticViewModel Semantic { get; }
    public PeopleViewModel People { get; }
    public VideoViewModel Video { get; }

    public List<string> CameraBrands { get; set; } = new();

    [ObservableProperty]
    private SourceNodeViewModel? _selectedSource;

    [ObservableProperty]
    private ImageItemViewModel? _selectedImage;

    [ObservableProperty]
    private TagItemViewModel? _selectedTag;

    [ObservableProperty]
    private AlbumItemViewModel? _selectedAlbum;

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private bool _isQuestionMode;

    [ObservableProperty]
    private string _questionAnswerText = "问答仅在本地索引上运行，不外发图片或元数据。";

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isIndexing;

    [ObservableProperty]
    private string _newTagName = "";

    [ObservableProperty]
    private SortField _sortBy = SortField.TakenDate;

    [ObservableProperty]
    private SortDirection _sortDir = SortDirection.Ascending;

    [ObservableProperty]
    private bool _showFilterPanel = true;

    [ObservableProperty]
    private string _outputText = "";

    [ObservableProperty]
    private bool _isLogVisible;

    [ObservableProperty]
    private DirectoryNodeViewModel? _selectedDirectory;

    [ObservableProperty]
    private string _selectedPhotoContext = "未选择照片";

    [ObservableProperty]
    private int _matchedCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    private bool _hasMoreResults;

    [ObservableProperty] private DateTime? _filterTakenAfter;
    [ObservableProperty] private DateTime? _filterTakenBefore;
    [ObservableProperty] private int? _filterMinWidth;
    [ObservableProperty] private int? _filterMinHeight;
    [ObservableProperty] private int? _filterMaxWidth;
    [ObservableProperty] private int? _filterMaxHeight;
    [ObservableProperty] private long? _filterMinFileSizeMb;
    [ObservableProperty] private long? _filterMaxFileSizeMb;
    [ObservableProperty] private string? _selectedCameraBrand;
    [ObservableProperty] private bool _includeMissing;

    public int TotalCount => Images.Count;

    public void ReplaceIndexEngine(IndexEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        StatusText = $"存储配置已应用：{AppServices.Storage.Scheme}";
    }
    public string ResultsSummary => MatchedCount > TotalCount
        ? $"已加载 {TotalCount} / 共 {MatchedCount} 张"
        : $"共 {MatchedCount} 张";
    public int DirectoryImageCount => DirectoryImages.Count;
    public bool IsLibraryEmpty => Sources.FirstOrDefault(source => source.IsAll)?.ImageCount == 0;
    public string ActiveFiltersText
    {
        get
        {
            var chips = new List<string>();
            if (ResolveGeoBounds() is { } place) chips.Add($"地域：{place.Name}（GPS 范围）");
            else if (!string.IsNullOrWhiteSpace(SearchQuery)) chips.Add($"关键词：{SearchQuery}");
            if (FilterTakenAfter is not null || FilterTakenBefore is not null) chips.Add($"日期：{FilterTakenAfter:yyyy-MM-dd}–{FilterTakenBefore:yyyy-MM-dd}");
            if (FilterMinWidth is not null || FilterMinHeight is not null || FilterMaxWidth is not null || FilterMaxHeight is not null)
                chips.Add($"尺寸：{FilterMinWidth ?? 0}×{FilterMinHeight ?? 0}–{FilterMaxWidth?.ToString() ?? "∞"}×{FilterMaxHeight?.ToString() ?? "∞"}");
            if (FilterMinFileSizeMb is not null || FilterMaxFileSizeMb is not null) chips.Add($"大小：{FilterMinFileSizeMb ?? 0}–{FilterMaxFileSizeMb?.ToString() ?? "∞"} MB");
            if (!string.IsNullOrWhiteSpace(SelectedCameraBrand)) chips.Add($"相机：{SelectedCameraBrand}");
            if (SelectedTag is not null) chips.Add($"标签：{SelectedTag.Tag.Name}");
            if (SelectedSource is { IsAll: false }) chips.Add($"来源：{SelectedSource.DisplayName}");
            if (IncludeMissing) chips.Add("包含缺失");
            return chips.Count == 0 ? "当前条件：全部活动图片" : "当前条件：" + string.Join(" · ", chips);
        }
    }
    public string MatchExplanation => ResolveGeoBounds() is { } place
        ? $"命中依据：照片 GPS 位于{place.Name}地域范围内"
        : string.IsNullOrWhiteSpace(SearchQuery)
            ? "命中依据：结构化筛选"
            : "命中依据：文件路径、相机或 GPS 全文匹配，并满足结构化筛选";

    public MainViewModel(PicerDatabase db, IndexEngine engine, IReverseGeocoder geocoder, PeopleViewModel people)
    {
        _db = db;
        _engine = engine;
        _geocoder = geocoder;
        People = people;
        Video = new VideoViewModel(AppServices.Videos, AppServices.VideoIndex, AppServices.VideoKeyframes,
            AppServices.VideoTranscription, AppServices.VideoFaceTimeline, AppServices.VideoSummaries);
        Semantic = new SemanticViewModel(db, () =>
        {
            LoadTags();
            _ = ApplySearchAndFilterAsync();
        });
        SelectedImages.CollectionChanged += (_, _) => Semantic.SetSelection(SelectedImages.Select(image => image.Record.Id));

        _searchDebounce = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300),
            IsEnabled = false
        };
        _searchDebounce.Tick += (s, e) =>
        {
            _searchDebounce.Stop();
            _ = ApplySearchAndFilterAsync();
        };

    }

    public async Task InitializeAsync()
    {
        var started = Stopwatch.GetTimestamp();
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await ReloadSourcesAsync();
        await LoadLookupValuesAsync();
        await LoadDirectoryTreeAsync();
        await ApplySearchAndFilterAsync();
        _ = Video.LoadAsync();
        _ = EnrichLocationHierarchyAsync();
        PicerEventSource.Log.StartupStage("first-page-ready", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    partial void OnSelectedSourceChanged(SourceNodeViewModel? value) => LoadImages();
    partial void OnSearchQueryChanged(string value)
    {
        if (!string.Equals(value.Trim(), _resolvedGeoQuery, StringComparison.OrdinalIgnoreCase))
        {
            _resolvedGeoQuery = null;
            _resolvedGeoBounds = null;
        }
        _searchDebounce.Stop();
        if (string.IsNullOrWhiteSpace(value))
        {
            _ = ApplySearchAndFilterAsync();
        }
        else
        {
            _searchDebounce.Start();
        }
    }
    partial void OnIsQuestionModeChanged(bool value)
    {
        QuestionAnswerText = value
            ? "本地问答已开启。可输入：“有多少张照片？”或“这些照片用了什么相机？”。仅查询 D 盘本地索引，不会外发内容。"
            : "本地问答已关闭。";
        StatusText = value ? "本地问答已开启，请在搜索框输入问题" : "本地问答已关闭";
        _ = ApplySearchAndFilterAsync();
    }
    partial void OnSortByChanged(SortField value) => _ = ApplySearchAndFilterAsync();
    partial void OnSortDirChanged(SortDirection value) => _ = ApplySearchAndFilterAsync();
    partial void OnFilterTakenAfterChanged(DateTime? value) => _ = ApplySearchAndFilterAsync();
    partial void OnFilterTakenBeforeChanged(DateTime? value) => _ = ApplySearchAndFilterAsync();
    partial void OnFilterMinWidthChanged(int? value) => _ = ApplySearchAndFilterAsync();
    partial void OnFilterMinHeightChanged(int? value) => _ = ApplySearchAndFilterAsync();
    partial void OnFilterMaxWidthChanged(int? value) => _ = ApplySearchAndFilterAsync();
    partial void OnFilterMaxHeightChanged(int? value) => _ = ApplySearchAndFilterAsync();
    partial void OnFilterMinFileSizeMbChanged(long? value) => _ = ApplySearchAndFilterAsync();
    partial void OnFilterMaxFileSizeMbChanged(long? value) => _ = ApplySearchAndFilterAsync();
    partial void OnSelectedCameraBrandChanged(string? value) => _ = ApplySearchAndFilterAsync();
    partial void OnSelectedTagChanged(TagItemViewModel? value) => _ = ApplySearchAndFilterAsync();
    partial void OnIncludeMissingChanged(bool value) => _ = ApplySearchAndFilterAsync();
    partial void OnSelectedDirectoryChanged(DirectoryNodeViewModel? value) => LoadImagesForSelectedDirectory();
    partial void OnSelectedImageChanged(ImageItemViewModel? value)
    {
        Semantic.SetSelection(SelectedImages.Count > 0 ? SelectedImages.Select(image => image.Record.Id) : value is null ? [] : [value.Record.Id]);
        _ = UpdateSelectedPhotoContextAsync(value?.Record);
    }

    private async Task UpdateSelectedPhotoContextAsync(ImageRecord? record)
    {
        _photoContextCts?.Cancel();
        var cancellation = _photoContextCts = new CancellationTokenSource();
        if(record is null){SelectedPhotoContext="未选择照片";return;}
        var time=record.TakenUtc is not null?$"拍摄时间：{record.TakenUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}":
            $"文件创建时间：{(record.CreatedUtc??record.ModifiedUtc).ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        var description=await UiWork.RunDatabaseAsync(()=>AppServices.CloudVisionRepository.GetLatestCompleted(record.Id)?.Result.Summary,cancellation.Token);
        if(cancellation.IsCancellationRequested)return;
        var descriptionText=string.IsNullOrWhiteSpace(description)?"":"    描述："+description;
        SelectedPhotoContext=$"{time}    拍摄地点：无GPS信息{descriptionText}";
        if(!GeoPlaceResolver.TryParseCoordinates(record.Gps,out var latitude,out var longitude))return;
        try{var place=await _geocoder.ReverseAsync(latitude,longitude,cancellation.Token);if(!cancellation.IsCancellationRequested)SelectedPhotoContext=$"{time}    拍摄地点：{place?.DisplayName??$"{latitude:F5}, {longitude:F5}"}{descriptionText}";}
        catch(OperationCanceledException){}catch{if(!cancellation.IsCancellationRequested)SelectedPhotoContext=$"{time}    拍摄地点：{latitude:F5}, {longitude:F5}{descriptionText}";}
    }

    public void ReloadSources()
    {
        var previous = SelectedSource?.SourceId;
        Sources.Clear();
        Sources.Add(new SourceNodeViewModel
        {
            SourceId = 0,
            IsAll = true,
            DisplayName = "所有图片",
            ImageCount = _db.GetImageCount()
        });
        foreach (var s in _db.GetSources())
        {
            Sources.Add(new SourceNodeViewModel
            {
                SourceId = s.Id,
                DisplayName = Path.GetFileName(s.Path),
                FullPath = s.Path,
                ImageCount = s.ImageCount
            });
        }
        SelectedSource = Sources.FirstOrDefault(s => s.SourceId == previous) ?? Sources.FirstOrDefault();
    }

    private async Task ReloadSourcesAsync()
    {
        var previous = SelectedSource?.SourceId;
        var snapshot = await UiWork.RunDatabaseAsync(() => (Count: _db.GetImageCount(), Sources: _db.GetSources().ToArray()));
        Sources.Clear();
        Sources.Add(new SourceNodeViewModel { SourceId = 0, IsAll = true, DisplayName = "所有图片", ImageCount = snapshot.Count });
        foreach (var source in snapshot.Sources)
            Sources.Add(new SourceNodeViewModel
            {
                SourceId = source.Id, DisplayName = Path.GetFileName(source.Path), FullPath = source.Path, ImageCount = source.ImageCount
            });
        SelectedSource = Sources.FirstOrDefault(source => source.SourceId == previous) ?? Sources.FirstOrDefault();
    }

    private async Task LoadLookupValuesAsync()
    {
        var snapshot = await UiWork.RunDatabaseAsync(() => (
            Cameras: IndexSearchEngine.GetDistinctValues(_db, "camera").Select(camera => camera.Split(' ')[0])
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Tags: SemanticEngine.GetTags(_db).ToArray()));
        CameraBrands = snapshot.Cameras;
        Tags.Clear();
        foreach (var tag in snapshot.Tags) Tags.Add(new TagItemViewModel(tag));
    }

    public void LoadImages()
    {
        _ = ApplySearchAndFilterAsync();
        OnPropertyChanged(nameof(IsLibraryEmpty));
    }

    public async Task ApplySearchAndFilterAsync()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var version = Interlocked.Increment(ref _searchVersion);
        var sourceId = SelectedSource is null || SelectedSource.IsAll ? (long?)null : SelectedSource.SourceId;
        var filters = BuildSearchFilters(sourceId);
        LocalQuestionResult? question = null;
        if (IsQuestionMode)
        {
            question = LocalQuestionInterpreter.Interpret(SearchQuery);
            filters.Query = "";
        }
        SearchPage page;
        try
        {
            page = await IndexSearchEngine.SearchPagedAsync(_db, filters, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (version == _searchVersion)
                StatusText = $"搜索失败：{ex.Message}";
            return;
        }
        if (version != _searchVersion || cts.IsCancellationRequested) return;
        OnPropertyChanged(nameof(ActiveFiltersText));
        OnPropertyChanged(nameof(MatchExplanation));
        MatchedCount = page.TotalCount;
        _searchCursor = page.NextCursor;
        HasMoreResults = _searchCursor is not null;
        Images.Clear();
        foreach (var rec in page.Items)
            Images.Add(new ImageItemViewModel(rec));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(ResultsSummary));
        if (question is not null)
        {
            QuestionAnswerText = question.Intent switch
            {
                LocalQuestionIntent.Count => $"回答：当前条件下共 {MatchedCount} 张图片。依据：{question.Explanation}。",
                LocalQuestionIntent.CameraSummary => $"回答：当前索引包含的相机品牌有 {string.Join("、", CameraBrands.DefaultIfEmpty("无相机信息"))}。依据：{question.Explanation}。",
                _ => $"暂不支持这个问题。{question.Explanation}。未将内容发送到外部服务。"
            };
        }
        StatusText = IsQuestionMode && string.IsNullOrWhiteSpace(SearchQuery)
            ? "本地问答已开启，请输入照片数量或相机相关问题"
            : SearchQuery.Length > 0
            ? (MatchedCount > TotalCount
                ? $"搜索「{SearchQuery}」：显示 {TotalCount} / 共 {MatchedCount} 条"
                : $"搜索「{SearchQuery}」：找到 {MatchedCount} 条")
            : (MatchedCount > TotalCount ? $"显示 {TotalCount} / 共 {MatchedCount} 张图片" : $"共 {MatchedCount} 张图片");
    }

    private SearchFilters BuildSearchFilters(long? sourceId = null)
    {
        var mention = IsQuestionMode ? null : People.FindMention(SearchQuery);
        var person = mention?.Person;
        var visual = VisualConceptCatalog.Parse(mention?.RemainingQuery ?? SearchQuery);
        var geoBounds = IsQuestionMode ? null : mention is null && visual.ConceptIds.Count == 0
            ? ResolveGeoBounds()
            : ResolveMentionGeoBounds(visual.RemainingQuery);
        return new()
    {
        Query = IsQuestionMode || geoBounds is not null || mention is not null || visual.ConceptIds.Count > 0 ? "" : SearchQuery,
        GeoBounds = geoBounds,
        PersonId = person?.Id,
        VisualConceptIds = visual.ConceptIds,
        SourceId = sourceId ?? (SelectedSource is null || SelectedSource.IsAll ? null : SelectedSource.SourceId),
        IncludeMissing = IncludeMissing,
        TakenAfter = FilterTakenAfter,
        TakenBefore = FilterTakenBefore,
        MinWidth = FilterMinWidth,
        MinHeight = FilterMinHeight,
        MaxWidth = FilterMaxWidth,
        MaxHeight = FilterMaxHeight,
        MinFileSize = FilterMinFileSizeMb * 1024 * 1024,
        MaxFileSize = FilterMaxFileSizeMb * 1024 * 1024,
        CameraBrand = SelectedCameraBrand,
        TagId = SelectedTag?.Tag.Id,
        SortBy = SortBy,
        SortDir = SortDir
    };
    }

    private GeoBounds? ResolveMentionGeoBounds(string remainingQuery)
    {
        if (string.IsNullOrWhiteSpace(remainingQuery)) return null;
        return GeoPlaceResolver.Resolve(remainingQuery) ??
            (string.Equals(SearchQuery.Trim(), _resolvedGeoQuery, StringComparison.OrdinalIgnoreCase)
                ? _resolvedGeoBounds
                : null);
    }


    [RelayCommand]
    private async Task ResolvePlaceAsync(string? query)
    {
        query = query?.Trim();
        if (string.IsNullOrWhiteSpace(query)) return;
        var mention = People.FindMention(query);
        var visual = VisualConceptCatalog.Parse(mention?.RemainingQuery ?? query);
        var placeQuery = visual.RemainingQuery;
        if (string.IsNullOrWhiteSpace(placeQuery))
        {
            await ApplySearchAndFilterAsync();
            return;
        }
        if (GeoPlaceResolver.Resolve(placeQuery) is not null)
        {
            await ApplySearchAndFilterAsync();
            return;
        }

        StatusText = $"正在识别地域“{query}”…";
        try
        {
            var bounds = await _geocoder.ResolvePlaceAsync(placeQuery);
            if (!string.Equals(SearchQuery.Trim(), query, StringComparison.OrdinalIgnoreCase)) return;
            if (bounds is null)
            {
                StatusText = $"未识别到地域“{query}”";
                return;
            }
            _resolvedGeoQuery = query;
            _resolvedGeoBounds = bounds;
            await ApplySearchAndFilterAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"地域识别失败：{ex.Message}";
        }
    }

    private GeoBounds? ResolveGeoBounds() => GeoPlaceResolver.Resolve(SearchQuery) ??
        (string.Equals(SearchQuery.Trim(), _resolvedGeoQuery, StringComparison.OrdinalIgnoreCase) ? _resolvedGeoBounds : null);

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private async Task LoadMoreAsync()
    {
        if (_searchCursor is null) return;
        var page = await IndexSearchEngine.SearchPagedAsync(_db, BuildSearchFilters(), cursor: _searchCursor);
        foreach (var rec in page.Items) Images.Add(new ImageItemViewModel(rec));
        _searchCursor = page.NextCursor;
        HasMoreResults = _searchCursor is not null;
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(ResultsSummary));
        StatusText = $"显示 {TotalCount} / 共 {page.TotalCount} 张图片";
    }

    private bool CanLoadMore() => HasMoreResults;

    [RelayCommand]
    private void ClearFilters()
    {
        FilterTakenAfter = null;
        FilterTakenBefore = null;
        FilterMinWidth = null;
        FilterMinHeight = null;
        FilterMaxWidth = null;
        FilterMaxHeight = null;
        FilterMinFileSizeMb = null;
        FilterMaxFileSizeMb = null;
        SelectedCameraBrand = null;
        SelectedTag = null;
        IncludeMissing = false;
        _ = ApplySearchAndFilterAsync();
    }

    public void LoadCameraBrands()
    {
        CameraBrands = IndexSearchEngine.GetDistinctValues(_db, "camera")
            .Select(c => c.Split(' ')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void LoadTags()
    {
        Tags.Clear();
        foreach (var tag in SemanticEngine.GetTags(_db)) Tags.Add(new TagItemViewModel(tag));
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        string sourcePath;
        if (string.Equals(AppServices.Storage.Scheme, "webdav", StringComparison.OrdinalIgnoreCase))
        {
            sourcePath = AppServices.Config.WebDAV?.BasePath ?? "/";
            StatusText = $"正在添加 WebDAV 来源：{sourcePath}";
        }
        else
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择要索引的图片文件夹" };
            StatusText = "请选择要添加的照片文件夹";
            if (dialog.ShowDialog(Application.Current.MainWindow) != true)
            {
                StatusText = "已取消添加文件夹";
                return;
            }
            sourcePath = dialog.FolderName;
        }

        var source = await UiWork.RunDatabaseAsync(() => _db.AddSource(sourcePath));
        await ReloadSourcesAsync();
        LoadImages();
        await RunIndexAsync([source]);
    }

    [RelayCommand]
    private async Task RemoveSourceAsync()
    {
        SourceNodeViewModel? node = null;

        if (SelectedSource is not null && !SelectedSource.IsAll)
            node = SelectedSource;
        else if (SelectedDirectory is not null)
            node = Sources.FirstOrDefault(s => !s.IsAll && SelectedDirectory.FullPath.StartsWith(s.FullPath, StringComparison.OrdinalIgnoreCase));

        if (node is null)
        {
            MessageBox.Show("请先在目录树中选择要移除的源文件夹", "移除索引源", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show($"确定移除索引源并删除其全部索引记录吗？\n{node.FullPath}\n（不会删除实际图片文件）",
                "移除索引源", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await UiWork.RunDatabaseAsync(() => _db.RemoveSource(node.SourceId));
        await ReloadSourcesAsync();
        await LoadDirectoryTreeAsync();
        LoadImages();
    }

    [RelayCommand]
    private async Task StartIndexAsync() => await RunIndexAsync(await UiWork.RunDatabaseAsync(() => _db.GetSources()));

    private async Task RunIndexAsync(IReadOnlyList<SourceFolder> sources)
    {
        if (IsIndexing) return;
        IsIndexing = true;
        IsBusy = true;
        _indexCts = new CancellationTokenSource();
        try
        {
            if (sources.Count == 0)
            {
                StatusText = "请先添加索引文件夹";
                return;
            }

            ClearOutput();
            AppendOutput("开始索引...");

            var progress = new Progress<IndexProgress>(p =>
            {
                StatusText = $"{p.Phase} {p.Done}/{p.Total}  {Path.GetFileName(p.CurrentFile)}";
                ProgressValue = p.Total > 0 ? (double)p.Done / p.Total * 100 : 0;
                if (p.Done % 50 == 0 || p.Done == p.Total)
                    AppendOutput($"{p.Phase} {p.Done}/{p.Total}");
            });

            var total = new IndexResult(0, 0, 0, 0);
            foreach (var source in sources)
            {
                if (_indexCts.Token.IsCancellationRequested) break;
                var r = await _engine.IndexSourceAsync(source, progress, _indexCts.Token);
                total = total with
                {
                    Scanned = total.Scanned + r.Scanned,
                    Indexed = total.Indexed + r.Indexed,
                    Skipped = total.Skipped + r.Skipped,
                    MarkedMissing = total.MarkedMissing + r.MarkedMissing
                };
            }

            AppendOutput($"完成：扫描 {total.Scanned}，新增 {total.Indexed}，跳过 {total.Skipped}，缺失 {total.MarkedMissing}");
            StatusText = $"索引完成 — 扫描 {total.Scanned}，新增 {total.Indexed}";
            ProgressValue = 100;
            await ReloadSourcesAsync();
            LoadImages();
            LoadDirectoryTreeCommand.Execute(null);

            if (total.Indexed > 0)
            {
                AppendOutput("已启动新增照片的人脸分析和自动归类");
                _ = People.ProcessNewPhotosAsync();
            }

            _ = EnrichLocationHierarchyAsync();

            if (total.MarkedMissing > 0)
                await HandleMissingAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText = "索引已取消；已完成的记录仍然有效。";
            AppendOutput("用户取消了索引任务");
        }
        catch (Exception ex)
        {
            StatusText = $"索引失败：{ex.Message}";
            AppendOutput($"索引失败：{ex}");
            MessageBox.Show($"索引未完成：{ex.Message}", "索引失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsIndexing = false;
            IsBusy = false;
            _indexCts?.Dispose();
            _indexCts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelIndex))]
    private void CancelIndex() => _indexCts?.Cancel();

    private bool CanCancelIndex() => IsIndexing && _indexCts is not null;

    private async Task EnrichLocationHierarchyAsync()
    {
        if (Interlocked.CompareExchange(ref _locationEnrichmentRunning, 1, 0) != 0) return;
        try
        {
            var progress = new Progress<LocationEnrichmentProgress>(value =>
            {
                StatusText = $"正在构建位置索引 {value.CompletedGroups}/{value.TotalGroups}，已更新 {value.SavedImages} 张";
            });
            var result = await AppServices.LocationHierarchy.EnrichPendingAsync(progress, _locationEnrichmentCts.Token);
            if (result.CoordinateGroups == 0) return;

            AppendOutput($"位置索引完成：更新 {result.SavedImages} 张，失败位置 {result.FailedGroups} 个");
            StatusText = result.FailedGroups == 0
                ? $"位置索引已更新 {result.SavedImages} 张照片"
                : $"位置索引更新 {result.SavedImages} 张，{result.FailedGroups} 个位置稍后重试";
            await ApplySearchAndFilterAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppendOutput($"位置索引构建失败：{exception.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _locationEnrichmentRunning, 0);
        }
    }

    partial void OnIsIndexingChanged(bool value) => CancelIndexCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private async Task IndexAllAsync() => await StartIndexAsync();

    [RelayCommand]
    private async Task VerifyAsync()
    {
        if (IsBusy || IsIndexing) return;
        IsBusy = true;
        try
        {
            StatusText = "正在检查文件是否存在...";
            var missing = await _engine.VerifyAsync();
            StatusText = missing.Count == 0 ? "检查完成，没有缺失文件" : $"发现 {missing.Count} 个缺失文件";
            await ReloadSourcesAsync();
            LoadImages();
            if (missing.Count > 0)
                await HandleMissingAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchQuery = "";
        _searchDebounce.Stop();
        _ = ApplySearchAndFilterAsync();
    }

    [RelayCommand]
    private void ClearOutput()
    {
        _logBuffer.Clear();
        OutputText = "";
    }

    [RelayCommand]
    private void ToggleLog() => IsLogVisible = !IsLogVisible;

    [RelayCommand]
    private void OpenDuplicates()
    {
        var vm = new DuplicateViewModel(_db, _engine, AppServices.FileOperations);
        var window = new DuplicateWindow(vm) { Owner = Application.Current.MainWindow };
        window.Show();
    }

    [RelayCommand]
    private void OpenSelected()
    {
        var path = SelectedImage?.Path;
        if (path is null) { StatusText = "请先选择图片"; return; }
        if (!File.Exists(path)) { StatusText = "原图片当前不可用，请运行“检查缺失”后重新定位"; return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception exception) { StatusText = $"无法打开图片：{exception.Message}"; }
    }

    [RelayCommand]
    private void ShowInExplorer()
    {
        var path = SelectedImage?.Path;
        if (path is null) { StatusText = "请先选择图片"; return; }
        if (!File.Exists(path)) { StatusText = "原图片当前不可用，请运行“检查缺失”后重新定位"; return; }
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception exception) { StatusText = $"无法在资源管理器中定位：{exception.Message}"; }
    }

    [RelayCommand]
    private async Task ShowImageInfoAsync()
    {
        if (SelectedImage is null) return;
        var image = SelectedImage;
        var record = image.Record;
        var timeText = record.TakenUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            ?? (record.ModifiedUtc == DateTime.MinValue ? "未知" : record.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "（文件修改时间）");
        var locationText = "无 GPS 信息";
        var attribution = "";
        var cloud = await UiWork.RunDatabaseAsync(() => AppServices.CloudVisionRepository.Get(record.Id, "bailian")
                    ?? AppServices.CloudVisionRepository.Get(record.Id, "zhipu"));
        var cloudText = cloud is { Status: "completed", Summary: not null }
            ? $"\n\n云端描述：{cloud.Value.Summary}"
            : cloud is not null ? $"\n\n云端分析状态：{cloud.Value.Status}" : "\n\n云端描述：尚未分析";

        if (GeoPlaceResolver.TryParseCoordinates(record.Gps, out var latitude, out var longitude))
        {
            locationText = $"{latitude:F6}, {longitude:F6}";
            StatusText = "正在查询详细地点…";
            try
            {
                var place = await _geocoder.ReverseAsync(latitude, longitude);
                if (place is not null)
                {
                    locationText = $"{place.DisplayName}\n坐标：{latitude:F6}, {longitude:F6}";
                    attribution = $"\n\n地点数据：{place.Attribution}";
                }
                else locationText += "\n未能取得城市/地区详情";
            }
            catch (Exception ex)
            {
                locationText += $"\n地点查询失败：{ex.Message}";
            }
        }

        StatusText = $"正在查看 {image.FileName} 的信息";
        MessageBox.Show(
            $"文件：{image.FileName}\n\n拍摄时间：{timeText}\n文件大小：{FormatFileSize(record.FileSize)}\n尺寸：{record.Width} × {record.Height}\n\n地点：{locationText}{attribution}{cloudText}",
            "图片信息", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand]
    private Task AnalyzeSelectedWithCloudAsync()
    {
        var selected = (SelectedImages.Count > 0 ? SelectedImages :
            SelectedImage is null ? [] : [SelectedImage]).Select(item => item.Record).DistinctBy(item => item.Id).ToList();
        return AnalyzeImagesWithCloudAsync(selected);
    }

    public async Task AnalyzeImagesWithCloudAsync(IReadOnlyList<ImageRecord> selected)
    {
        if (selected.Count == 0)
        {
            MessageBox.Show("请先选择要描述的照片。", "云端照片分析", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var useComplexModel = await UiWork.RunDatabaseAsync(() => selected.Any(image => AppServices.VisionRepository.GetFaceCount(image.Id) > 1 ||
            AppServices.VisualConceptRepository.GetScores(image.Id).Any(score => score.Confidence < .65f)));
        var configured = AppServices.CreateCloudVisionService(useComplexModel);
        if (configured is null)
        {
            MessageBox.Show("云端分析尚未启用，或尚未保存API Key。请先打开“云端分析设置”。", "云端照片分析",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var count = configured.Value.Policy.ClampCount(selected.Count);
        var preciseText = configured.Value.Policy.IncludePreciseGps ? "会提供精确坐标" : "不会提供精确坐标，只提供本地解析后的地点文字";
        var estimated = DomesticCloudVisionPricing.Calculate(configured.Value.Analyzer.Provider,
            configured.Value.Analyzer.ModelVersion, count * 1200L, count * 200L).EstimatedCostYuan;
        var confirmation = MessageBox.Show(
            $"即将通过{configured.Value.ProviderLabel}分析 {count} 张照片，粗略估算费用 ¥{estimated:0.0000}。\n每日/每月预算：¥{configured.Value.Policy.DailyBudgetYuan:0.##} / ¥{configured.Value.Policy.MonthlyBudgetYuan:0.##}。\n\n上传的是最长边1280像素、已清除EXIF的副本；{preciseText}。原始照片不会被修改。\n\n是否继续？",
            "确认云端照片分析", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            configured.Value.Analyzer.Dispose();
            return;
        }

        _cloudCts?.Cancel();
        _cloudCts?.Dispose();
        _photoContextCts?.Cancel();
        _cloudCts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            var candidates = await UiWork.RunDatabaseAsync(() => selected.Take(count).Select(image => (
                image,
                AppServices.VisualConceptRepository.GetScores(image.Id),
                "user-requested")).ToArray());
            var progress = new Progress<(int Completed, int Total)>(value =>
                StatusText = $"云端描述：{value.Completed}/{value.Total}");
            var result = await configured.Value.Service.AnalyzeAsync(candidates, progress, _cloudCts.Token, configured.Value.Policy);
            StatusText = $"云端描述完成：成功 {result.Completed}，缓存跳过 {result.Skipped}，失败 {result.Failed}，预算拦截 {result.BudgetBlocked}";
            await LoadDirectoryTreeAsync();
            MessageBox.Show(StatusText, "云端照片分析", MessageBoxButton.OK,
                result.Failed == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (OperationCanceledException) { StatusText = "云端照片分析已取消"; }
        catch (Exception exception)
        {
            StatusText = $"云端照片分析失败：{exception.Message}";
            MessageBox.Show(StatusText + "\n\n可在“任务中心”查看单张照片的详细错误。", "云端照片分析",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            configured.Value.Analyzer.Dispose();
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ScanVideosAsync()
    {
        IsBusy = true;
        StatusText = "正在扫描视频文件…";
        try
        {
            var count = await AppServices.VideoIndex.ScanLocalSourcesAsync();
            StatusText = $"视频索引完成：发现 {count} 个视频；FFmpeg " +
                         (AppServices.VideoKeyframes is null ? "尚未就绪" : "已就绪");
            MessageBox.Show(StatusText, "视频索引", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            StatusText = $"视频索引失败：{exception.Message}";
            MessageBox.Show(StatusText, "视频索引", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DeleteSelectedFilesAsync()
    {
        var selected=(SelectedImages.Count>0?SelectedImages:SelectedImage is null?[]:[SelectedImage])
            .Select(item=>item.Record).DistinctBy(item=>item.Id).Where(item=>item.Status==ImageStatus.Active).ToList();
        if(selected.Count==0){MessageBox.Show("请先在图片网格中选择一张或多张图片。","删除所选",MessageBoxButton.OK,MessageBoxImage.Information);return;}
        IsBusy=true; StatusText="正在生成安全删除快照…";
        try
        {
            var selections=await Task.Run(()=>selected.Where(item=>File.Exists(item.Path)).Select(item=>new AuthorizedFileSelection(
                item.Id,item.Path,new FileInfo(item.Path).Length,new FileInfo(item.Path).LastWriteTimeUtc,
                item.Sha256??FileHasher.ComputeSha256(item.Path))).ToArray());
            if(selections.Length!=selected.Count){MessageBox.Show("部分原文件已不存在，未执行删除。请先运行“检查缺失”。","删除所选",MessageBoxButton.OK,MessageBoxImage.Warning);return;}
            var plan=new DeletionPlan(Guid.NewGuid(),DateTime.UtcNow,selections);
            var preview=string.Join(Environment.NewLine,plan.Items.Take(8).Select(item=>item.Path));
            var more=plan.FileCount>8?$"\n……另有 {plan.FileCount-8} 个文件":"";
            var confirm=MessageBox.Show($"将把 {plan.FileCount} 个原始文件移入 Windows 回收站，并在成功后删除对应索引和缩略图。\n\n{preview}{more}\n\n此操作不会直接永久清空回收站。是否继续？",
                "确认删除原始文件",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No);
            if(confirm!=MessageBoxResult.Yes){StatusText="已取消删除，未更改文件、索引或缩略图";return;}
            var result=await AppServices.FileOperations.MoveToRecycleBinAsync(plan);
            foreach(var item in result.Items.Where(item=>item.Success))await _engine.RemoveIndexRecordAsync(item.Selection.ImageId);
            SelectedImages.Clear(); SelectedImage=null; await ReloadSourcesAsync(); await ApplySearchAndFilterAsync(); await LoadDirectoryTreeAsync();
            if(SelectedDirectory is not null)LoadImagesForSelectedDirectory();
            StatusText=$"删除完成：成功 {result.SucceededCount}，失败 {result.FailedCount}";
            if(result.FailedCount>0){var errors=string.Join(Environment.NewLine,result.Items.Where(item=>!item.Success).Take(8).Select(item=>$"{item.Selection.Path}：{item.ErrorMessage}"));MessageBox.Show(errors,"部分文件删除失败；其索引已保留",MessageBoxButton.OK,MessageBoxImage.Warning);}
        }
        catch(Exception ex){StatusText=$"删除失败：{ex.Message}";MessageBox.Show(StatusText,"删除所选",MessageBoxButton.OK,MessageBoxImage.Error);}
        finally{IsBusy=false;}
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)Math.Max(0, bytes);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.##} {units[unit]}";
    }

    [RelayCommand]
    private async Task LoadDirectoryTreeAsync()
    {
        var selectedPath = SelectedDirectory?.FullPath;
        var sources = await UiWork.RunDatabaseAsync(() => _db.GetSources().ToArray());
        DirectoryTree.Clear();
        foreach (var source in sources)
        {
            var node = new DirectoryNodeViewModel
            {
                Name = Path.GetFileName(source.Path),
                FullPath = source.Path,
                Icon = "📁",
                ImageCount = source.ImageCount,
                IsExpanded = true
            };
            DirectoryTree.Add(node);
            _ = ExpandDirectoryAsync(node);
        }
        SelectedDirectory = DirectoryTree.FirstOrDefault(node =>
            string.Equals(node.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase)) ?? DirectoryTree.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ExpandDirectoryAsync(DirectoryNodeViewModel node)
    {
        if (node.Children.Count > 0 || node.IsLoaded) return;
        node.IsLoaded = true;
        try
        {
            var dirs = await Task.Run(() =>
            {
                var result = new List<(string Path, string Name, int ImageCount)>();
                try
                {
                    foreach (var dir in Directory.GetDirectories(node.FullPath))
                    {
                        var dirInfo = new DirectoryInfo(dir);
                        if (dirInfo.Attributes.HasFlag(FileAttributes.Hidden)) continue;

                        var imageCount = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                            .Count(f => SupportedFormats.IsSupported(f));

                        if (imageCount > 0)
                            result.Add((dir, dirInfo.Name, imageCount));
                    }
                }
                catch (UnauthorizedAccessException) { }
                return result;
            });

            foreach (var (path, name, count) in dirs)
            {
                node.Children.Add(new DirectoryNodeViewModel
                {
                    Name = name,
                    FullPath = path,
                    Icon = "📂",
                    ImageCount = count
                });
            }
        }
        catch { }
    }

    public async Task LoadSceneAnchorsAsync(DirectoryNodeViewModel node, bool force=false)
    {
        if(node.IsSceneAnchor)return;
        if(node.SceneAnchorsLoaded&&!force)return;
        foreach(var anchor in node.Children.Where(child=>child.IsSceneAnchor).ToList())node.Children.Remove(anchor);
        var recursive=_directoryClassification.IncludesDescendants(node.FullPath);node.ClassificationIncludesDescendants=recursive;
        var clusters=await UiWork.RunDatabaseAsync(()=>new SceneTimeClusterService(_db).Build(node.FullPath,recursive));
        foreach(var cluster in clusters)node.Children.Add(new DirectoryNodeViewModel{Name=cluster.DisplayName,FullPath=node.FullPath,Icon="⚓",ImageCount=cluster.ImageIds.Count,IsSceneAnchor=true,SceneName=cluster.SceneName,AnchorImageIds=cluster.ImageIds});
        node.SceneAnchorsLoaded=true;
    }

    public async Task SetDirectoryClassificationModeAsync(DirectoryNodeViewModel node,bool includeDescendants)
    {
        if(node.IsSceneAnchor)return;_directoryClassification.Set(node.FullPath,includeDescendants);
        void Reset(DirectoryNodeViewModel current){if(current.IsSceneAnchor)return;current.ClassificationIncludesDescendants=_directoryClassification.IncludesDescendants(current.FullPath);current.SceneAnchorsLoaded=false;foreach(var anchor in current.Children.Where(child=>child.IsSceneAnchor).ToList())current.Children.Remove(anchor);foreach(var child in current.Children.Where(child=>!child.IsSceneAnchor))Reset(child);}
        Reset(node);await LoadSceneAnchorsAsync(node,true);
    }

    public async Task DescribeUnclassifiedSceneAsync(DirectoryNodeViewModel node)
    {
        if (!node.IsSceneAnchor || !string.Equals(node.SceneName, "未分类场景", StringComparison.Ordinal))
        {
            MessageBox.Show("请在目录树中右键选择一个“未分类场景”节点。", "场景描述", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var images = await UiWork.RunDatabaseAsync(() => node.AnchorImageIds.Select(id => _db.GetImage(id))
            .Where(image => image is { Status: ImageStatus.Active } && File.Exists(image.Path)).Cast<ImageRecord>().ToArray());
        if (images.Length == 0) { MessageBox.Show("该场景没有当前可读取的照片。", "场景描述"); return; }
        var service = new SceneGroupDescriptionService(_db, AppServices.CloudVisionRepository);
        var plan = await UiWork.RunDatabaseAsync(() => service.BuildPlan(node.FullPath, images, maximumSubgroups: 5, maximumRepresentatives: 3));
        var missing = await UiWork.RunDatabaseAsync(() => plan.Representatives.Where(image => AppServices.CloudVisionRepository.GetLatestCompleted(image.Id) is null).ToArray());
        var preview = $"该时间段共有 {plan.Images.Count} 张照片，已细分为 {plan.Subgroups.Count} 组，" +
            $"并从前、中、后均匀选择 {plan.Representatives.Count} 张代表照片。\n" +
            $"其中 {missing.Length} 张需要调用已配置的云端视觉 API，其余将复用已有描述。\n\n是否继续？";
        if (MessageBox.Show(preview, "描述未分类场景", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (missing.Length > 0) await AnalyzeImagesWithCloudAsync(missing);
        var summary = await UiWork.RunDatabaseAsync(() => service.Summarize(plan));
        if (summary is null)
        {
            MessageBox.Show("代表照片没有可用描述。请在任务中心查看失败原因后重试。", "场景描述", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var accept = MessageBox.Show($"建议场景：{summary.SceneName}\n\n{summary.Summary}\n\n" +
            $"搜索词：{string.Join("、", summary.SearchTerms)}\n\n接受后将显示在目录树中，是否应用？",
            "审核场景总结", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (accept != MessageBoxResult.Yes) return;
        await UiWork.RunDatabaseAsync(() => service.SaveAccepted(plan, summary));
        StatusText = $"已应用场景描述：{summary.SceneName}";
        await LoadDirectoryTreeAsync();
    }

    private async void LoadImagesForSelectedDirectory()
    {
        if (SelectedDirectory is null) return;
        _directoryLoadCts?.Cancel();
        var cts = _directoryLoadCts = new CancellationTokenSource();
        var selectedDirectory = SelectedDirectory;
        DirectoryImages.Clear();
        try
        {
            var records = await UiWork.RunDatabaseAsync(() => selectedDirectory.IsSceneAnchor
                ? selectedDirectory.AnchorImageIds.Select(id => _db.GetImage(id)).Where(image => image is not null).Cast<ImageRecord>()
                    .OrderBy(SceneTimeClusterService.EffectiveTime).ThenBy(image => image.Id).ToList()
                : _db.GetActiveImagesUnderDirectory(selectedDirectory.FullPath), cts.Token);
            var indexedPaths = records.Select(record => record.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            await UiWork.AddInBatchesAsync(records.Where(image => image.Status == ImageStatus.Active),
                image => DirectoryImages.Add(new ImageItemViewModel(image)), cts.Token);
            OnPropertyChanged(nameof(DirectoryImageCount));
            StatusText = $"[{selectedDirectory.Name}] 已显示 {DirectoryImages.Count} 张索引照片";

            var unindexedFiles = selectedDirectory.IsSceneAnchor ? [] : await Task.Run(() =>
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
                };
                return Directory.EnumerateFiles(selectedDirectory.FullPath, "*", options)
                    .Where(SupportedFormats.IsSupported)
                    .Where(path => !indexedPaths.Contains(path))
                    .Select(path =>
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        var info = new FileInfo(path);
                        return new ImageRecord
                        {
                            Path = path,
                            FileSize = info.Exists ? info.Length : 0,
                            ModifiedUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue,
                            Status = ImageStatus.Active
                        };
                    })
                    .ToList();
            }, cts.Token);

            if (cts.IsCancellationRequested || !ReferenceEquals(SelectedDirectory, selectedDirectory))
                return;
            await UiWork.AddInBatchesAsync(unindexedFiles,
                rec => DirectoryImages.Add(new ImageItemViewModel(rec)), cts.Token);

            if (selectedDirectory.IsSceneAnchor && DirectoryImages.Count > 0) SelectedImage = DirectoryImages[0];

            OnPropertyChanged(nameof(DirectoryImageCount));
            StatusText = $"[{SelectedDirectory.Name}] {DirectoryImages.Count} 张";
        }
        catch (OperationCanceledException) { }
        catch (UnauthorizedAccessException) { }
        catch (Exception exception) { StatusText = $"目录照片加载失败：{exception.Message}"; }
    }

    private async Task HandleMissingAsync()
    {
        var missing = await UiWork.RunDatabaseAsync(() => _db.GetMissingImages());
        if (missing.Count == 0) return;
        var window = new MissingFilesWindow(_engine, missing) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
        await ReloadSourcesAsync();
        LoadImages();
    }

    private void AppendOutput(string line)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        _logBuffer.AppendLine($"[{ts}] {line}");

        while (_logBuffer.ToString().Count(c => c == '\n') > MaxLogLines)
        {
            var text = _logBuffer.ToString();
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0)
                _logBuffer.Clear().Append(text[(firstNewline + 1)..]);
        }

        OutputText = _logBuffer.ToString();
    }

    public void Dispose()
    {
        _locationEnrichmentCts.Cancel();
        _searchCts?.Cancel();
        _directoryLoadCts?.Cancel();
        _indexCts?.Cancel();
        _cloudCts?.Cancel();
        _cloudCts?.Dispose();
        _locationEnrichmentCts.Dispose();
        People.Dispose();
    }
}

public partial class DirectoryNodeViewModel : ObservableObject
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string Icon { get; set; } = "📁";
    public int ImageCount { get; set; }
    public bool IsLoaded { get; set; }
    public bool IsExpanded { get; set; }
    public bool IsSelected { get; set; }
    public bool IsSceneAnchor { get; set; }
    public string SceneName { get; set; } = "";
    public bool SceneAnchorsLoaded { get; set; }
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ClassificationModeLabel))] private bool _classificationIncludesDescendants;
    public string ClassificationModeLabel => ClassificationIncludesDescendants ? "（含子目录）" : "";
    public IReadOnlyList<long> AnchorImageIds { get; set; } = [];
    public ObservableCollection<DirectoryNodeViewModel> Children { get; } = new();
}
