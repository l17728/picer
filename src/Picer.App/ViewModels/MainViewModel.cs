using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Data;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Picer.Core;
using Picer.Core.Data;
using Picer.Core.Diagnostics;
using Picer.Core.Indexing;
using Picer.Core.Geocoding;
using Picer.Core.Models;
using Picer.Core.Logging;
using Picer.Core.Search;
using Picer.Core.Vision;
using Picer.Core.Video;
using Picer.App.Infrastructure;
using Picer.Core.FileOperations;
using Picer.Core.Hashing;
using Picer.App.Views;

namespace Picer.App.ViewModels;

public enum DirectoryMediaFilter { Images, Videos, All }

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
    private CancellationTokenSource? _aiQuestionCts;
    private CancellationTokenSource? _directoryLoadCts;
    private CancellationTokenSource? _sceneContextCts;
    private int _directoryLoadVersion;
    private CancellationTokenSource? _cloudCts;
    private CancellationTokenSource? _photoContextCts;
    private readonly CancellationTokenSource _locationEnrichmentCts = new();
    private int _locationEnrichmentRunning;
    private string[] _timelineAnchors = [];
    private long _searchVersion;
    private string? _resolvedGeoQuery;
    private readonly DirectoryClassificationPreferences _directoryClassification = new(AppPaths.DirectoryClassificationSettingsPath);
    private GeoBounds? _resolvedGeoBounds;
    private readonly List<ImageRecord> _searchLoadedRecords = [];
    private readonly SemaphoreSlim _videoPosterGate = new(2, 2);

    public ObservableCollection<SourceNodeViewModel> Sources { get; } = new();
    public ObservableCollection<ImageItemViewModel> Images { get; } = new();
    public ObservableCollection<TagItemViewModel> Tags { get; } = new();
    public ObservableCollection<AlbumItemViewModel> Albums { get; } = new();
    public ObservableCollection<DirectoryNodeViewModel> DirectoryTree { get; } = new();
    public ObservableCollection<ImageItemViewModel> DirectoryImages { get; } = new();
    public ObservableCollection<object> DirectoryTimelineItems { get; } = new();
    public ListCollectionView DirectoryImagesView { get; }
    public System.Collections.IEnumerable DirectoryDisplayItems => IsTimelineAlbumSelected ? DirectoryTimelineItems : DirectoryImagesView;
    public ObservableCollection<ImageItemViewModel> SelectedImages { get; } = new();
    public ObservableCollection<AiVideoResultViewModel> AiVideoResults { get; } = new();
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
    private bool _includeVideosInAiQuestion;

    [ObservableProperty]
    private string _aiQuestionInput = "";

    [ObservableProperty]
    private bool _isAiQuestionRunning;

    [ObservableProperty]
    private string _questionAnswerText = "## AI 问答\n\n输入完整问题后按 Enter 或点击“提交问题”。输入过程中不会调用模型。";

    [ObservableProperty]
    private bool _showSceneContext;

    [ObservableProperty]
    private string _sceneContextText = "";

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _isLocationEnriching;

    [ObservableProperty]
    private double _locationProgressValue;

    [ObservableProperty]
    private double _locationProgressMaximum = 1;

    [ObservableProperty]
    private string _locationProgressText = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isIndexing;

    [ObservableProperty]
    private string _newTagName = "";

    [ObservableProperty]
    private SortField _sortBy = SortField.TakenDate;

    [ObservableProperty]
    private SortDirection _sortDir = SortDirection.Descending;

    [ObservableProperty]
    private bool _isChronologicalAscending;

    [ObservableProperty]
    private bool _isTimelineAlbumSelected;

    [ObservableProperty]
    private int _timelineGranularityIndex;

    [ObservableProperty]
    private string _currentTimelineAnchor = "";

    [ObservableProperty]
    private string _timelinePositionText = "";

    [ObservableProperty]
    private bool _canNavigatePreviousTimeline;

    [ObservableProperty]
    private bool _canNavigateNextTimeline;

    [ObservableProperty]
    private object? _timelineScrollTarget;

    [ObservableProperty]
    private bool _showFilterPanel = true;

    [ObservableProperty]
    private string _outputText = "";

    [ObservableProperty]
    private bool _isLogVisible;

    [ObservableProperty]
    private DirectoryNodeViewModel? _selectedDirectory;

    [ObservableProperty]
    private DirectoryMediaFilter _directoryMediaFilter = DirectoryMediaFilter.Images;

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

    public int TotalCount => Images.Sum(image => image.BurstCount);

    public void ReplaceIndexEngine(IndexEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        StatusText = $"存储配置已应用：{AppServices.Storage.Scheme}";
    }
    public string ResultsSummary => MatchedCount > TotalCount
        ? $"已加载 {TotalCount} / 共 {MatchedCount} 张"
        : $"共 {MatchedCount} 张";
    public int DirectoryImageCount => DirectoryImages.Sum(image => image.BurstCount);
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
            AppServices.VideoTranscription, AppServices.VideoFaceTimeline, AppServices.VideoSummaries, AppServices.People);
        Video.SetIdentityChangedCallback(People.RefreshSharedIdentityAsync);
        Semantic = new SemanticViewModel(db, () =>
        {
            LoadTags();
            _ = ApplySearchAndFilterAsync();
            _ = LoadDirectoryTreeAsync();
        });
        SelectedImages.CollectionChanged += (_, _) => Semantic.SetSelection(SelectedImages.Where(image => !image.IsVideo).Select(image => image.Record.Id));
        DirectoryImagesView = (ListCollectionView)CollectionViewSource.GetDefaultView(DirectoryImages);

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
        await ApplySearchAndFilterAsync();
        await LoadLookupValuesAsync();
        await LoadDirectoryTreeAsync();
        if (!AppServices.ServiceMode)
        {
            _ = Video.LoadAsync();
            _ = EnrichLocationHierarchyAsync();
            _ = RebuildTextIndexAsync();
        }
        PicerEventSource.Log.StartupStage("first-page-ready", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (!AppServices.ServiceMode)
            BackfillBurstGroupsAsync().Forget();
    }

    public async Task RefreshFromRemoteMirrorAsync()
    {
        if (!AppServices.ServiceMode) return;
        await ReloadSourcesAsync();
        await ApplySearchAndFilterAsync();
        await LoadDirectoryTreeAsync();
        _ = Video.LoadAsync();
        StatusText = "远程索引已同步";
    }

    private async Task BackfillBurstGroupsAsync()
    {
        try
        {
            var sources = await UiWork.RunDatabaseAsync(() => _db.GetSources().ToArray());
            var rebuilt = 0;
            foreach (var source in sources)
            {
                var changed = await UiWork.RunDatabaseAsync("BurstGroups.Backfill", () =>
                {
                    var service = new BurstGroupService(_db);
                    return service.NeedsRebuild(source.Id) ? service.RebuildSource(source.Id) : -1;
                });
                if (changed >= 0) rebuilt += changed;
            }
            if (rebuilt == 0) return;
            await ApplySearchAndFilterAsync();
            if (SelectedDirectory is not null) LoadImagesForSelectedDirectory();
            StatusText = $"连拍照片索引已更新：{rebuilt} 组";
        }
        catch (Exception exception)
        {
            AppLogger.Error("后台构建连拍照片索引失败", exception);
        }
    }

    partial void OnSelectedSourceChanged(SourceNodeViewModel? value) => LoadImages();

    public void SetDirectoryMediaFilter(int selectedIndex)
    {
        DirectoryMediaFilter = selectedIndex switch
        {
            1 => DirectoryMediaFilter.Videos,
            2 => DirectoryMediaFilter.All,
            _ => DirectoryMediaFilter.Images
        };
        ReloadDirectoryMedia();
    }

    public void ReloadDirectoryMedia()
    {
        if (SelectedDirectory is not null) LoadImagesForSelectedDirectory();
    }
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
            StatusText = $"准备搜索“{value.Trim()}”…";
            _searchDebounce.Start();
        }
    }
    partial void OnIsQuestionModeChanged(bool value)
    {
        if (value && string.IsNullOrWhiteSpace(QuestionAnswerText))
            QuestionAnswerText = "## AI 问答\n\n输入完整问题后按 Enter 或点击“提交问题”。输入过程中不会调用模型。";
        StatusText = value ? "AI 问答面板已打开；等待提交问题" : "AI 问答面板已收起";
    }
    partial void OnSortByChanged(SortField value) => _ = ApplySearchAndFilterAsync();
    partial void OnSortDirChanged(SortDirection value) => _ = ApplySearchAndFilterAsync();
    partial void OnIsChronologicalAscendingChanged(bool value)
    {
        SortBy = SortField.TakenDate;
        SortDir = value ? SortDirection.Ascending : SortDirection.Descending;
        LoadImagesForSelectedDirectory();
        OnPropertyChanged(nameof(ChronologicalOrderLabel));
    }
    public string ChronologicalOrderLabel => IsChronologicalAscending ? "时间升序（最新在下）" : "时间降序（最新在上）";
    partial void OnTimelineGranularityIndexChanged(int value) => RefreshDirectoryTimelineView(resetPosition: true);
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
    partial void OnSelectedDirectoryChanged(DirectoryNodeViewModel? value)
    {
        IsTimelineAlbumSelected = value?.IsTimelineAlbum == true;
        OnPropertyChanged(nameof(DirectoryDisplayItems));
        _ = LoadSceneContextAsync(value);
        LoadImagesForSelectedDirectory();
    }

    private async Task LoadSceneContextAsync(DirectoryNodeViewModel? node)
    {
        _sceneContextCts?.Cancel(); _sceneContextCts?.Dispose();
        var cancellation = _sceneContextCts = new CancellationTokenSource();
        ShowSceneContext = node?.IsSceneAnchor == true;
        SceneContextText = ShowSceneContext ? $"场景：{node!.SceneName}　正在读取摘要…" : "";
        if (!ShowSceneContext) return;
        try
        {
            var text = await UiWork.RunDatabaseAsync(() =>
            {
                var images = node!.AnchorImageIds.Select(id => _db.GetImage(id))
                    .Where(image => image is { Status: ImageStatus.Active }).Cast<ImageRecord>().ToArray();
                if (images.Length == 0) return $"场景：{node.SceneName}　没有可用照片记录";
                var service = new SceneGroupDescriptionService(_db, AppServices.CloudVisionRepository);
                var accepted = service.GetAccepted(SceneGroupDescriptionService.CreateFingerprint(images));
                if (accepted is not null) return $"场景：{accepted.SceneName}　摘要：{accepted.Summary}";
                var descriptions = images.Select(image => AppServices.CloudVisionRepository.GetLatestCompleted(image.Id)?.Result.Summary)
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.CurrentCultureIgnoreCase).Take(2).ToArray();
                return descriptions.Length > 0
                    ? $"场景：{node.SceneName}　照片描述：{string.Join("；", descriptions)}（尚未生成场景级摘要）"
                    : $"场景：{node.SceneName}　尚未生成场景级摘要，照片也没有可用描述";
            }, cancellation.Token);
            if (!cancellation.IsCancellationRequested && ReferenceEquals(SelectedDirectory, node)) SceneContextText = text;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!cancellation.IsCancellationRequested && ReferenceEquals(SelectedDirectory, node))
                SceneContextText = $"场景：{node!.SceneName}　摘要读取失败：{exception.Message}";
            AppLogger.Error($"读取场景摘要失败：{node!.Name}", exception);
        }
    }
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
                DisplayName = AppServices.ServiceMode ? $"远程 · {Path.GetFileName(s.Path)}" : Path.GetFileName(s.Path),
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
                SourceId = source.Id, DisplayName = AppServices.ServiceMode ? $"远程 · {Path.GetFileName(source.Path)}" : Path.GetFileName(source.Path), FullPath = source.Path, ImageCount = source.ImageCount
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
        _aiQuestionCts?.Cancel();
        _aiQuestionCts?.Dispose();
        var cts = _searchCts = new CancellationTokenSource();
        var version = Interlocked.Increment(ref _searchVersion);
        var sourceId = SelectedSource is null || SelectedSource.IsAll ? (long?)null : SelectedSource.SourceId;
        var filters = BuildSearchFilters(sourceId);
        SearchPage page;
        try
        {
            page = await AppServices.MediaSearch.ExecutePagedAsync(filters, cancellationToken: cts.Token);
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
        _searchLoadedRecords.Clear();
        _searchLoadedRecords.AddRange(page.Items);
        try
        {
            var rendered = 0;
            var displayItems = await BuildBurstAwareItemsAsync(page.Items, cts.Token);
            foreach (var row in displayItems.Chunk(8))
            {
                cts.Token.ThrowIfCancellationRequested();
                if (version != _searchVersion) return;
                foreach (var item in row) Images.Add(item);
                rendered += row.Sum(item => item.BurstCount);
                OnPropertyChanged(nameof(TotalCount));
                OnPropertyChanged(nameof(ResultsSummary));
                if (rendered < page.Items.Count)
                    StatusText = $"正在渐进显示照片：{rendered} / {page.Items.Count}";
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(ResultsSummary));
        StatusText = SearchQuery.Length > 0
            ? (MatchedCount > TotalCount
                ? $"搜索「{SearchQuery}」：显示 {TotalCount} / 共 {MatchedCount} 条"
                : $"搜索「{SearchQuery}」：找到 {MatchedCount} 条")
            : (MatchedCount > TotalCount ? $"显示 {TotalCount} / 共 {MatchedCount} 张图片" : $"共 {MatchedCount} 张图片");
    }

    [RelayCommand]
    public async Task SubmitAiQuestionAsync()
    {
        var question = AiQuestionInput.Trim();
        if (question.Length == 0) { StatusText = "请输入问题后再提交"; return; }
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var version = Interlocked.Increment(ref _searchVersion);
        IsAiQuestionRunning = true;
        try { await ApplyAiQuestionAsync(version, cts, question); }
        finally { IsAiQuestionRunning = false; }
    }

    [RelayCommand]
    private void CancelAiQuestion()
    {
        _aiQuestionCts?.Cancel();
        StatusText = "正在停止 AI 问答…";
    }

    private async Task ApplyAiQuestionAsync(long version, CancellationTokenSource searchCancellation, string question)
    {
        using var operation = AppLogger.BeginOperation("ai-question.execute",
            ("includeVideos", IncludeVideosInAiQuestion), ("queryLength", question.Length));
        _aiQuestionCts?.Cancel(); _aiQuestionCts?.Dispose();
        _aiQuestionCts = CancellationTokenSource.CreateLinkedTokenSource(searchCancellation.Token);
        var token = _aiQuestionCts.Token;
        using var chat = AppServices.CreateAiChatClient(AiFeatureIds.AiQuestion);
        if (chat is null)
        {
            QuestionAnswerText = "## AI 问答尚未配置\n\n请在“AI 模型设置 → AI 问答与媒体检索”中选择可用模型。";
            StatusText = "AI 问答模型尚未配置";
            return;
        }
        var includeVideos = IncludeVideosInAiQuestion;
        try
        {
            StatusText = "AI 问答：正在理解问题…";
            var progress = new Progress<(string Stage, int Percent)>(value =>
            {
                ProgressValue = value.Percent; StatusText = $"AI 问答：{value.Stage}（{value.Percent}%）";
            });
            AppLogger.Info($"AiQuestion started; includeVideos={includeVideos}; queryLength={question.Length}");
            var service = new AiMediaRetrievalService(_db, AppServices.People, AppServices.Videos,
                AppServices.CloudVisionRepository, chat);
            var answer = await service.AskAsync(question, includeVideos, DateTime.Today, progress, token);
            if (version != _searchVersion || token.IsCancellationRequested) return;
            QuestionAnswerText = answer.Markdown;
            MatchedCount = answer.Photos.Count;
            _searchCursor = null; HasMoreResults = false;
            Images.Clear(); _searchLoadedRecords.Clear(); _searchLoadedRecords.AddRange(answer.Photos);
            var photoItems = await BuildBurstAwareItemsAsync(answer.Photos, token);
            await UiWork.AddInBatchesAsync(photoItems, Images.Add, batchSize: 8, cancellationToken: token);
            AiVideoResults.Clear();
            foreach (var video in answer.Videos) AiVideoResults.Add(new AiVideoResultViewModel(video));
            OnPropertyChanged(nameof(TotalCount)); OnPropertyChanged(nameof(ResultsSummary));
            StatusText = $"AI 问答完成：照片 {answer.Photos.Count} 张，视频 {answer.Videos.Count} 个";
            AppLogger.Info($"AiQuestion completed; photos={answer.Photos.Count}; videos={answer.Videos.Count}; hasPersonFilter={!string.IsNullOrWhiteSpace(answer.Plan.PersonName)}; hasLocationFilter={!string.IsNullOrWhiteSpace(answer.Plan.Location)}; keywordCount={answer.Plan.Keywords.Count}");
        }
        catch (OperationCanceledException) { operation.Cancel("superseded-or-canceled"); AppLogger.Info("AiQuestion superseded or canceled"); }
        catch (Exception exception)
        {
            QuestionAnswerText = $"## 查询失败\n\n{exception.Message}";
            StatusText = $"AI 问答失败：{exception.Message}";
            operation.Fail(exception);
            AppLogger.Error("AiQuestion failed", exception);
        }
    }

    private SearchFilters BuildSearchFilters(long? sourceId = null)
    {
        var mention = People.FindMention(SearchQuery);
        var person = mention?.Person;
        var visual = VisualConceptCatalog.Parse(mention?.RemainingQuery ?? SearchQuery);
        var geoBounds = mention is null && visual.ConceptIds.Count == 0
            ? ResolveGeoBounds()
            : ResolveMentionGeoBounds(visual.RemainingQuery);
        return new()
    {
        Query = geoBounds is not null || mention is not null || visual.ConceptIds.Count > 0 ? "" : SearchQuery,
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
        var page = await AppServices.MediaSearch.ExecutePagedAsync(BuildSearchFilters(), cursor: _searchCursor);
        _searchLoadedRecords.AddRange(page.Items);
        var rebuilt = await BuildBurstAwareItemsAsync(_searchLoadedRecords, _searchCts?.Token ?? default);
        Images.Clear();
        await UiWork.AddInBatchesAsync(rebuilt, Images.Add, batchSize: 8);
        _searchCursor = page.NextCursor;
        HasMoreResults = _searchCursor is not null;
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(ResultsSummary));
        StatusText = $"显示 {TotalCount} / 共 {page.TotalCount} 张图片";
    }

    private bool CanLoadMore() => HasMoreResults;

    private async Task<IReadOnlyList<ImageItemViewModel>> BuildBurstAwareItemsAsync(
        IReadOnlyList<ImageRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0) return [];
        var groups = await UiWork.RunDatabaseAsync("BurstGroups.Load",
            () => new BurstGroupService(_db).GetGroupsForImages(records.Select(record => record.Id)), cancellationToken);
        var recordsById = records.GroupBy(record => record.Id).ToDictionary(group => group.Key, group => group.First());
        var groupByImage = groups.Values.SelectMany(group => group.ImageIds.Select(id => (id, group)))
            .Where(pair => recordsById.ContainsKey(pair.id)).ToDictionary(pair => pair.id, pair => pair.group);
        var visibleByGroup = records.Where(record => groupByImage.ContainsKey(record.Id))
            .GroupBy(record => groupByImage[record.Id].Id)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var handledGroups = new HashSet<long>();
        var result = new List<ImageItemViewModel>(records.Count);
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!groupByImage.TryGetValue(record.Id, out var group))
            {
                result.Add(new ImageItemViewModel(record, cancellationToken));
                continue;
            }
            if (!handledGroups.Add(group.Id)) continue;
            var visibleMembers = visibleByGroup[group.Id];
            if (visibleMembers.Length < 2)
            {
                result.Add(new ImageItemViewModel(record, cancellationToken));
                continue;
            }
            var stack = new ImageItemViewModel(visibleMembers[0], cancellationToken);
            stack.ConfigureBurst(visibleMembers, group.StartedUtc, group.EndedUtc);
            result.Add(stack);
        }
        return result;
    }

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
        using var operation = AppLogger.BeginOperation("index.run", ("sourceCount", sources.Count));
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
            operation.Cancel("user-canceled");
            StatusText = "索引已取消；已完成的记录仍然有效。";
            AppendOutput("用户取消了索引任务");
        }
        catch (Exception ex)
        {
            operation.Fail(ex);
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

    private async Task RebuildTextIndexAsync()
    {
        try
        {
            var progress = new Progress<(int Done, int Total)>(value =>
            {
                if (value.Total > 0 && value.Done < value.Total)
                    StatusText = $"正在更新文字索引 {value.Done}/{value.Total}（期间搜索结果可能不完整）";
            });
            var changed = await Task.Run(() => new Picer.Core.Search.MediaTextIndexRepository(_db).RebuildFromCloudAnalyses(progress: progress));
            if (changed > 0)
                AppendOutput($"文字索引已更新 {changed} 条（含地理位置与人物标记）");
        }
        catch (Exception exception)
        {
            AppLogger.Error("文字索引重建失败", exception);
        }
    }

    private async Task EnrichLocationHierarchyAsync()
    {
        if (Interlocked.CompareExchange(ref _locationEnrichmentRunning, 1, 0) != 0) return;
        IsLocationEnriching = true;
        LocationProgressText = "正在读取带 GPS 的照片…";
        try
        {
            var progress = new Progress<LocationEnrichmentProgress>(value =>
            {
                LocationProgressMaximum = Math.Max(1, value.TotalGroups);
                LocationProgressValue = value.CompletedGroups;
                LocationProgressText = $"GPS 反查 {value.CompletedGroups}/{value.TotalGroups}，已更新 {value.SavedImages} 张\n{value.CurrentPlace}";
                StatusText = $"正在构建位置索引 {value.CompletedGroups}/{value.TotalGroups}，已更新 {value.SavedImages} 张";
                if (value.CompletedGroups % 10 == 0) _ = LoadDirectoryTreeAsync();
            });
            var result = await Task.Run(
                () => AppServices.LocationHierarchy.EnrichPendingAsync(progress, _locationEnrichmentCts.Token),
                _locationEnrichmentCts.Token);
            if (result.CoordinateGroups == 0) return;

            AppendOutput($"位置索引完成：更新 {result.SavedImages} 张，失败位置 {result.FailedGroups} 个");
            if (result.SavedImages > 0)
            {
                AppendOutput("正在将地理位置写入文字索引…");
                await RebuildTextIndexAsync();
            }
            StatusText = result.FailedGroups == 0
                ? $"位置索引已更新 {result.SavedImages} 张照片"
                : $"位置索引更新 {result.SavedImages} 张，{result.FailedGroups} 个位置稍后重试";
            LocationProgressText = result.FailedGroups == 0
                ? $"位置解析完成，已更新 {result.SavedImages} 张"
                : $"本轮更新 {result.SavedImages} 张，{result.FailedGroups} 个地点未查到，可再次解析";
            await ApplySearchAndFilterAsync();
            await LoadDirectoryTreeAsync();
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
            IsLocationEnriching = false;
            Interlocked.Exchange(ref _locationEnrichmentRunning, 0);
        }
    }

    public async Task ParsePendingLocationsAsync()
    {
        if (Volatile.Read(ref _locationEnrichmentRunning) != 0)
        {
            StatusText = "位置解析正在后台进行，请稍候；完成后目录树会自动刷新";
            return;
        }
        StatusText = "正在解析“待解析位置”中的照片…";
        await EnrichLocationHierarchyAsync();
    }

    [RelayCommand]
    private async Task EditSelectedLocationAsync()
    {
        var images = (SelectedImages.Count > 0
                ? SelectedImages.ToArray()
                : SelectedImage is null ? [] : [SelectedImage])
            .Where(image => !image.IsVideo)
            .DistinctBy(image => image.Record.Id).ToArray();
        if (images.Length == 0) return;
        var choices = await UiWork.RunDatabaseAsync(() => _db.GetLocationHierarchyChoices().ToArray());
        if (choices.Length == 0)
        {
            MessageBox.Show("当前还没有可选的地理节点，请先解析位置。", "编辑地点",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new EditLocationWindow(choices, images.Length) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;
        StatusText = images.Length == 1 ? "正在更新照片地点…" : $"正在批量更新 {images.Length} 张照片的地点…";
        await UiWork.RunDatabaseAsync(() => _db.SetImageLocations(images.Select(image => image.Record.Id), dialog.Result));
        await LoadDirectoryTreeAsync();
        await ApplySearchAndFilterAsync();
        StatusText = images.Length == 1
            ? $"已更新“{images[0].FileName}”的地点：{dialog.Result.DisplayName}"
            : $"已批量更新 {images.Length} 张照片的地点：{dialog.Result.DisplayName}";
    }

    [RelayCommand]
    private async Task EditSelectedTagsAsync()
    {
        var images = (SelectedImages.Count > 0
                ? SelectedImages.ToArray()
                : SelectedImage is null ? [] : [SelectedImage])
            .Where(image => !image.IsVideo)
            .DistinctBy(image => image.Record.Id).ToArray();
        if (images.Length == 0) return;
        var imageIds = images.Select(image => image.Record.Id).ToArray();
        var snapshot = await UiWork.RunDatabaseAsync(() =>
        {
            var all = SemanticEngine.GetTags(_db).ToArray();
            var counts = all.ToDictionary(tag => tag.Id, _ => 0);
            foreach (var imageId in imageIds)
                foreach (var tag in SemanticEngine.GetImageTags(_db, imageId)) counts[tag.Id]++;
            return all.Select(tag => (Tag: tag, AssignedCount: counts[tag.Id])).ToArray();
        });
        var dialog = new EditPhotoTagsWindow(images.Length, snapshot) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;
        await UiWork.RunDatabaseAsync(() =>
        {
            var known = SemanticEngine.GetTags(_db);
            foreach (var item in dialog.Items.Where(item => item.IsChecked is not null))
            {
                var tag = known.FirstOrDefault(existing => string.Equals(existing.Name, item.Name,
                    StringComparison.CurrentCultureIgnoreCase));
                if (tag is null && item.IsChecked == true)
                {
                    tag = SemanticEngine.AddTag(_db, item.Name);
                    known.Add(tag);
                }
                if (tag is null) continue;
                foreach (var imageId in imageIds)
                {
                    if (item.IsChecked == true) SemanticEngine.AssignTag(_db, imageId, tag.Id);
                    else SemanticEngine.RemoveTag(_db, imageId, tag.Id);
                }
            }
        });
        LoadTags();
        Semantic.LoadTags();
        await ApplySearchAndFilterAsync();
        StatusText = $"已更新 {images.Length} 张照片的标签；标签可直接作为搜索关键字";
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
        if (image.VideoRecord is { } video)
        {
            var duration = video.DurationSeconds is double seconds ? TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss") : "未知";
            MessageBox.Show($"文件：{Path.GetFileName(video.Path)}\n路径：{video.Path}\n类型：视频\n时长：{duration}\n分辨率：{video.Width}×{video.Height}\n大小：{video.FileSize / 1024d / 1024d:F1} MB",
                "视频信息", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var record = image.Record;
        var timeText = record.TakenUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            ?? (record.ModifiedUtc == DateTime.MinValue ? "未知" : record.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "（文件修改时间）");
        var locationText = "无 GPS 信息";
        var attribution = "";
        var cloud = await UiWork.RunDatabaseAsync(() => AppServices.CloudVisionRepository.GetLatestCompleted(record.Id));
        var markedPeople = await UiWork.RunDatabaseAsync(() => AppServices.People.GetConfirmedPersonNamesForImage(record.Id));
        var peopleText = markedPeople.Count == 0 ? "无已标记人物" : string.Join("、", markedPeople);
        var cloudText = cloud is not null
            ? $"\n\nAI 描述（{cloud.Value.Provider} / {cloud.Value.ModelVersion}）：{cloud.Value.Result.Summary}"
            : "\n\nAI 描述：尚未分析";

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
            $"文件：{image.FileName}\n\n拍摄时间：{timeText}\n文件大小：{FormatFileSize(record.FileSize)}\n尺寸：{record.Width} × {record.Height}\n\n标记人物：{peopleText}\n\n地点：{locationText}{attribution}{cloudText}",
            "图片信息", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand]
    private Task AnalyzeSelectedWithCloudAsync()
    {
        var selected = (SelectedImages.Count > 0 ? SelectedImages :
            SelectedImage is null ? [] : [SelectedImage]).Select(item => item.Record).DistinctBy(item => item.Id).ToList();
        return AnalyzeImagesWithCloudAsync(selected);
    }

    public async Task AnalyzeImagesWithCloudAsync(IReadOnlyList<ImageRecord> selected, string featureId = AiFeatureIds.PhotoAnalysis,
        bool refreshDirectoryTree = true, bool showCompletionDialog = true)
    {
        if (selected.Count == 0)
        {
            MessageBox.Show("请先选择要描述的照片。", "云端照片分析", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var configured = AppServices.CreateCloudVisionService(featureId);
        if (configured is null)
        {
            MessageBox.Show("AI 分析尚未启用、模型未配置，或云端模型尚未保存 API Key。请先打开“AI 模型设置”。", "照片内容分析",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var count = configured.Value.Policy.ClampCount(selected.Count);
        var preciseText = configured.Value.Policy.IncludePreciseGps ? "会提供精确坐标" : "不会提供精确坐标，只提供本地解析后的地点文字";
        var estimated = DomesticCloudVisionPricing.Calculate(configured.Value.Analyzer.Provider,
            configured.Value.Analyzer.ModelVersion, count * 1200L, count * 200L).EstimatedCostYuan;
        var confirmationText = configured.Value.IsLocal
            ? $"即将通过 {configured.Value.ProviderLabel} 在本机分析 {count} 张照片。\n\n照片只发送到 127.0.0.1 的本地模型服务，不上传云端、不产生 API 费用；原始照片不会被修改。\n\n是否继续？"
            : $"即将通过{configured.Value.ProviderLabel}分析 {count} 张照片，粗略估算费用 ¥{estimated:0.0000}。\n每日/每月预算：¥{configured.Value.Policy.DailyBudgetYuan:0.##} / ¥{configured.Value.Policy.MonthlyBudgetYuan:0.##}。\n\n上传的是最长边1280像素、已清除EXIF的副本；{preciseText}。原始照片不会被修改。\n\n是否继续？";
        var confirmation = MessageBox.Show(confirmationText,
            configured.Value.IsLocal ? "确认本地照片分析" : "确认云端照片分析",
            MessageBoxButton.YesNo, configured.Value.IsLocal ? MessageBoxImage.Question : MessageBoxImage.Warning);
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
            if (refreshDirectoryTree) await LoadDirectoryTreeAsync();
            if (showCompletionDialog) MessageBox.Show(StatusText, "云端照片分析", MessageBoxButton.OK,
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
            var count = await UiWork.RunDatabaseAsync(() => AppServices.VideoIndex.ScanLocalSources());
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
            .Where(item=>!item.IsVideo).Select(item=>item.Record).DistinctBy(item=>item.Id).Where(item=>item.Status==ImageStatus.Active).ToList();
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
        var selectedAlbumId = SelectedDirectory?.AlbumId;
        var snapshot = await UiWork.RunDatabaseAsync(() => (
            Sources: _db.GetSources().ToArray(),
            Albums: SemanticEngine.GetAlbums(_db)
                .Select(album => (Album: album, Images: SemanticEngine.GetAlbumImages(_db, album.Id).ToArray())).ToArray(),
            Locations: _db.GetImageLocationHierarchyRows().ToArray()));
        AppLogger.Info($"Directory tree snapshot: sources={snapshot.Sources.Length}, albums={snapshot.Albums.Length}, albumImages={snapshot.Albums.Sum(item => item.Images.Length)}, locations={snapshot.Locations.Length}, serviceMode={AppServices.ServiceMode}");
        DirectoryTree.Clear();
        foreach (var source in snapshot.Sources)
        {
            var node = new DirectoryNodeViewModel
            {
                Name = AppServices.ServiceMode ? $"远程 · {Path.GetFileName(source.Path)}" : Path.GetFileName(source.Path),
                FullPath = source.Path,
                Icon = "📁",
                ImageCount = source.ImageCount,
                IsExpanded = true
            };
            DirectoryTree.Add(node);
            _ = ExpandDirectoryAsync(node);
        }
        var regularAlbums = snapshot.Albums.Where(item => !IsTimelineAlbum(item.Album)).ToArray();
        var timelineAlbums = snapshot.Albums.Where(item => IsTimelineAlbum(item.Album)).ToArray();
        var albumsRoot = new DirectoryNodeViewModel
        {
            Name = "我的相册", Icon = "📚", IsAlbumRoot = true, IsExpanded = true,
            ImageCount = regularAlbums.Sum(item => item.Images.Length),
            AnchorImageIds = regularAlbums.SelectMany(item => item.Images).Select(image => image.Id).Distinct().ToArray()
        };
        foreach (var item in regularAlbums)
            albumsRoot.Children.Add(new DirectoryNodeViewModel
            {
                Name = item.Album.Name, Icon = "🖼", AlbumId = item.Album.Id, ImageCount = item.Images.Length
            });
        DirectoryTree.Add(albumsRoot);

        var lightConeRoot = new DirectoryNodeViewModel
        {
            Name = "光锥之内", Icon = "⌛", IsAlbumRoot = true, IsExpanded = true, IsTimelineAlbum = true,
            ImageCount = timelineAlbums.Sum(item => item.Images.Length),
            AnchorImageIds = timelineAlbums.SelectMany(item => item.Images).Select(image => image.Id).Distinct().ToArray()
        };
        foreach (var item in timelineAlbums)
            lightConeRoot.Children.Add(new DirectoryNodeViewModel
            {
                Name = item.Album.Name, Icon = "🕰", AlbumId = item.Album.Id, ImageCount = item.Images.Length,
                IsTimelineAlbum = true
            });
        DirectoryTree.Add(lightConeRoot);

        DirectoryTree.Add(BuildGeographyTree(snapshot.Locations));

        SelectedDirectory = selectedAlbumId is not null
            ? albumsRoot.Children.Concat(lightConeRoot.Children).FirstOrDefault(node => node.AlbumId == selectedAlbumId)
            : DirectoryTree.FirstOrDefault(node => !node.IsAlbumRoot &&
                string.Equals(node.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase)) ??
              DirectoryTree.FirstOrDefault(node => !node.IsAlbumRoot);
    }

    private static bool IsTimelineAlbum(Album album) =>
        string.Equals(album.Description, "时间线相册（自动生成）", StringComparison.Ordinal);

    private static DirectoryNodeViewModel BuildGeographyTree(IReadOnlyList<ImageLocationHierarchyRow> rows)
    {
        var earth = new DirectoryNodeViewModel
        {
            Name = "太阳系第三行星（地球）", Icon = "🌍", IsGeoNode = true, IsGeoRoot = true,
            IsExpanded = false, ImageCount = rows.Count, AnchorImageIds = rows.Select(row => row.ImageId).ToArray()
        };
        var childMaps = new Dictionary<DirectoryNodeViewModel, Dictionary<string, DirectoryNodeViewModel>>();
        foreach (var row in rows)
        {
            var levels = new[] { row.Country, row.Province, row.City, row.County, row.District, row.Town, row.Village }
                .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()).ToArray();
            if (levels.Length == 0) levels = [row.HasGps ? "待解析位置" : "未知位置"];

            var parent = earth;
            foreach (var level in levels)
            {
                if (!childMaps.TryGetValue(parent, out var children))
                    childMaps[parent] = children = new(StringComparer.OrdinalIgnoreCase);
                if (!children.TryGetValue(level, out var child))
                {
                    child = new DirectoryNodeViewModel
                    {
                        Name = level, Icon = "📍", IsGeoNode = true,
                        IsPendingLocation = parent.IsGeoRoot && string.Equals(level, "待解析位置", StringComparison.Ordinal)
                    };
                    children[level] = child;
                    parent.Children.Add(child);
                }
                child.MutableAnchorImageIds.Add(row.ImageId);
                child.ImageCount = child.MutableAnchorImageIds.Count;
                parent = child;
            }
        }
        foreach (var node in FlattenGeoNodes(earth).Skip(1))
            if (node.MutableAnchorImageIds.Count > 0) node.AnchorImageIds = node.MutableAnchorImageIds.ToArray();
        return earth;
    }

    private static IEnumerable<DirectoryNodeViewModel> FlattenGeoNodes(DirectoryNodeViewModel node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in FlattenGeoNodes(child)) yield return descendant;
    }

    public IReadOnlyList<string> GetVoiceLocationPaths()
    {
        var root = DirectoryTree.FirstOrDefault(node => node.IsGeoRoot);
        if (root is null) return [];
        var result = new List<string>();
        AddVoiceLocationPaths(root, root.Name, result);
        return result;
    }

    public bool OpenVoiceLocation(string path)
    {
        var root = DirectoryTree.FirstOrDefault(node => node.IsGeoRoot);
        if (root is null) return false;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // LLMs may return a localized name (e.g. 长崎县/長崎県) rather than
        // the exact stored node path. Resolve by the unique suffix before
        // walking the tree, but never fall back to an unrelated node.
        if (parts.Length > 0 && !string.Equals(parts[0], root.Name, StringComparison.OrdinalIgnoreCase))
        {
            var candidate = EnumerateDirectoryPaths(root, root.Name)
                .FirstOrDefault(p => p.EndsWith('/' + parts[^1], StringComparison.OrdinalIgnoreCase) ||
                    p.Contains(parts[^1], StringComparison.OrdinalIgnoreCase));
            if (candidate is null) return false;
            parts = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        if (parts.Length == 0 || !string.Equals(parts[0], root.Name, StringComparison.OrdinalIgnoreCase)) return false;
        var current = root;
        current.IsExpanded = true;
        foreach (var part in parts.Skip(1))
        {
            var next = current.Children.FirstOrDefault(node => string.Equals(node.Name, part, StringComparison.OrdinalIgnoreCase))
                ?? current.Children.FirstOrDefault(node => node.Name.Contains(part, StringComparison.OrdinalIgnoreCase) || part.Contains(node.Name, StringComparison.OrdinalIgnoreCase));
            if (next is null) return false;
            current = next;
            current.IsExpanded = true;
        }
        SelectedDirectory = current;
        return true;
    }

    private static IEnumerable<string> EnumerateDirectoryPaths(DirectoryNodeViewModel node, string path)
    {
        yield return path;
        foreach (var child in node.Children)
            foreach (var nested in EnumerateDirectoryPaths(child, $"{path}/{child.Name}")) yield return nested;
    }

    private static void AddVoiceLocationPaths(DirectoryNodeViewModel node, string path, ICollection<string> result)
    {
        result.Add(path);
        foreach (var child in node.Children) AddVoiceLocationPaths(child, $"{path}/{child.Name}", result);
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
                            .Count(f => SupportedFormats.IsSupported(f) || VideoFormats.IsSupported(f));

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
        StatusText = force ? $"正在重新生成 {node.Name} 的场景分类…" : $"正在加载 {node.Name} 的场景分类…";
        try
        {
            var recursive=_directoryClassification.IncludesDescendants(node.FullPath);node.ClassificationIncludesDescendants=recursive;
            var clusters=await UiWork.RunDatabaseAsync(()=>new SceneTimeClusterService(_db).Build(node.FullPath,recursive));
            foreach(var anchor in node.Children.Where(child=>child.IsSceneAnchor).ToList())node.Children.Remove(anchor);
            foreach(var cluster in clusters)node.Children.Add(new DirectoryNodeViewModel{Name=cluster.DisplayName,FullPath=node.FullPath,Icon="⚓",ImageCount=cluster.ImageIds.Count,IsSceneAnchor=true,SceneName=cluster.SceneName,AnchorImageIds=cluster.ImageIds});
            node.SceneAnchorsLoaded=true;
            node.IsExpanded=true;
            StatusText = clusters.Count == 0 ? $"{node.Name} 没有可分类的照片" : $"{node.Name} 已生成 {clusters.Count} 个场景";
        }
        catch(Exception ex)
        {
            node.SceneAnchorsLoaded=false;
            StatusText=$"{node.Name} 场景分类失败：{ex.Message}";
            AppLogger.Error($"场景分类失败：{node.FullPath}", ex);
        }
    }

    public async Task SetDirectoryClassificationModeAsync(DirectoryNodeViewModel node,bool includeDescendants)
    {
        if(node.IsSceneAnchor)return;_directoryClassification.Set(node.FullPath,includeDescendants);
        void Reset(DirectoryNodeViewModel current){if(current.IsSceneAnchor)return;current.ClassificationIncludesDescendants=_directoryClassification.IncludesDescendants(current.FullPath);current.SceneAnchorsLoaded=false;foreach(var anchor in current.Children.Where(child=>child.IsSceneAnchor).ToList())current.Children.Remove(anchor);foreach(var child in current.Children.Where(child=>!child.IsSceneAnchor))Reset(child);}
        Reset(node);await LoadSceneAnchorsAsync(node,true);
    }

    public async Task DeleteAlbumAsync(DirectoryNodeViewModel node)
    {
        if (node.AlbumId is not { } albumId) return;
        var answer = MessageBox.Show(
            $"确定删除相册“{node.Name}”吗？\n\n只会删除相册及其照片关联，不会删除照片文件、缩略图或图片索引。",
            "删除相册", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        await UiWork.RunDatabaseAsync(() => SemanticEngine.DeleteAlbum(_db, albumId));
        Semantic.LoadAlbums();
        SelectedDirectory = null;
        DirectoryImages.Clear();
        OnPropertyChanged(nameof(DirectoryImageCount));
        await LoadDirectoryTreeAsync();
        StatusText = $"已删除相册“{node.Name}”；照片和索引未受影响";
    }

    public async Task DescribeUnclassifiedSceneAsync(DirectoryNodeViewModel node)
    {
        if (!node.IsSceneAnchor)
        {
            await DescribeUnclassifiedScenesInDirectoryAsync(node);
            return;
        }
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
        if (missing.Length > 0) await AnalyzeImagesWithCloudAsync(missing, AiFeatureIds.SceneDescription,
            refreshDirectoryTree: false, showCompletionDialog: false);
        using var summarizer = AppServices.CreateSceneGroupSummarizer();
        if (summarizer is null)
        {
            MessageBox.Show("场景总结模型尚未配置。请在“AI 模型设置”中为“未分类场景描述”选择服务商和模型。",
                "场景描述", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SceneGroupDescription? summary;
        try
        {
            StatusText = $"正在通过 {summarizer.Provider} / {summarizer.ModelVersion} 总结代表照片描述…";
            summary = await service.SummarizeWithAiAsync(plan, summarizer);
        }
        catch (Exception ex)
        {
            StatusText = $"场景总结失败：{ex.Message}";
            MessageBox.Show(StatusText, "场景描述", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (summary is null)
        {
            MessageBox.Show("代表照片没有可用描述。请在任务中心查看失败原因后重试。", "场景描述", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var accept = MessageBox.Show($"建议场景：{summary.SceneName}\n\n{summary.Summary}\n\n" +
            $"搜索词：{string.Join("、", summary.SearchTerms)}\n\n接受后将显示在目录树中，是否应用？",
            "审核场景总结", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (accept != MessageBoxResult.Yes) return;
        var parent = EnumerateDirectoryNodes(DirectoryTree).FirstOrDefault(candidate => candidate.Children.Contains(node));
        await UiWork.RunDatabaseAsync(() => service.SaveAccepted(plan, summary));
        StatusText = $"已应用场景描述：{summary.SceneName}";
        if (parent is null) return;
        await LoadSceneAnchorsAsync(parent, true);
        var refreshed = parent.Children.FirstOrDefault(candidate => candidate.IsSceneAnchor &&
            candidate.AnchorImageIds.Count == node.AnchorImageIds.Count &&
            candidate.AnchorImageIds.Order().SequenceEqual(node.AnchorImageIds.Order()));
        parent.IsExpanded = true;
        SelectedDirectory = refreshed ?? parent;
        if (refreshed is not null) refreshed.IsSelected = true;
    }

    public async Task EditSceneDescriptionAsync(DirectoryNodeViewModel node)
    {
        if (!node.IsSceneAnchor) return;
        var service = new SceneGroupDescriptionService(_db, AppServices.CloudVisionRepository);
        var images = await UiWork.RunDatabaseAsync(() => node.AnchorImageIds.Select(id => _db.GetImage(id))
            .Where(image => image is { Status: ImageStatus.Active }).Cast<ImageRecord>().ToArray());
        if (images.Length == 0) { StatusText = "该场景没有可编辑的照片记录"; return; }
        var plan = await UiWork.RunDatabaseAsync(() => service.BuildPlan(node.FullPath, images));
        var accepted = await UiWork.RunDatabaseAsync(() => service.GetAccepted(plan.Fingerprint));
        var window = new EditSceneDescriptionWindow(accepted?.SceneName ?? node.SceneName, accepted?.Summary ?? "")
        {
            Owner = Application.Current.MainWindow
        };
        if (window.ShowDialog() != true) return;
        await UiWork.RunDatabaseAsync(() =>
        {
            if (accepted is null)
            {
                service.SaveAccepted(plan, new SceneGroupDescription(plan.Fingerprint, window.SceneTitle,
                    window.SceneSummary, [window.SceneTitle], "user", "manual-edit", "manual-edit-v1", "accepted"));
            }
            else service.UpdateAcceptedText(plan.Fingerprint, window.SceneTitle, window.SceneSummary);
        });
        var parent = EnumerateDirectoryNodes(DirectoryTree).FirstOrDefault(candidate => candidate.Children.Contains(node));
        if (parent is not null)
        {
            await LoadSceneAnchorsAsync(parent, true);
            var refreshed = parent.Children.FirstOrDefault(candidate => candidate.IsSceneAnchor &&
                candidate.AnchorImageIds.Order().SequenceEqual(node.AnchorImageIds.Order()));
            parent.IsExpanded = true;
            SelectedDirectory = refreshed ?? parent;
            if (refreshed is not null) refreshed.IsSelected = true;
        }
        StatusText = $"已更新场景“{window.SceneTitle}”的标题和摘要";
    }

    private async Task DescribeUnclassifiedScenesInDirectoryAsync(DirectoryNodeViewModel directory)
    {
        StatusText = $"正在读取“{directory.Name}”中的未分类场景…";
        if (!directory.SceneAnchorsLoaded) await LoadSceneAnchorsAsync(directory, false);
        var anchors = directory.Children.Where(child => child.IsSceneAnchor &&
            string.Equals(child.SceneName, "未分类场景", StringComparison.Ordinal)).ToArray();
        if (anchors.Length == 0)
        {
            StatusText = $"“{directory.Name}”中没有待描述的未分类场景";
            MessageBox.Show(StatusText, "场景描述", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var service = new SceneGroupDescriptionService(_db, AppServices.CloudVisionRepository);
        var plans = new List<SceneGroupPlan>();
        foreach (var anchor in anchors)
        {
            var images = await UiWork.RunDatabaseAsync(() => anchor.AnchorImageIds.Select(id => _db.GetImage(id))
                .Where(image => image is { Status: ImageStatus.Active } && File.Exists(image.Path)).Cast<ImageRecord>().ToArray());
            if (images.Length == 0) continue;
            plans.Add(await UiWork.RunDatabaseAsync(() => service.BuildPlan(anchor.FullPath, images,
                maximumSubgroups: 5, maximumRepresentatives: 3)));
        }
        if (plans.Count == 0)
        {
            StatusText = "未分类场景中的照片当前均不可读取";
            MessageBox.Show(StatusText, "场景描述", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var missing = await UiWork.RunDatabaseAsync(() => plans.SelectMany(plan => plan.Representatives)
            .GroupBy(image => image.Id).Select(group => group.First())
            .Where(image => AppServices.CloudVisionRepository.GetLatestCompleted(image.Id) is null).ToArray());
        var preview = $"目录“{directory.Name}”中有 {plans.Count} 个尚未描述的场景，" +
            $"将使用 {plans.Sum(plan => plan.Representatives.Count)} 张代表照片；其中 {missing.Length} 张需要调用已配置的视觉模型。\n\n" +
            "生成成功的场景名称和摘要将直接应用；已有照片描述会增量复用。是否继续？";
        if (MessageBox.Show(preview, "批量描述未分类场景", MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        if (missing.Length > 0)
            await AnalyzeImagesWithCloudAsync(missing, AiFeatureIds.SceneDescription,
                refreshDirectoryTree: false, showCompletionDialog: false);
        using var summarizer = AppServices.CreateSceneGroupSummarizer();
        if (summarizer is null)
        {
            MessageBox.Show("场景总结模型尚未配置。请在“AI 模型设置”中为“未分类场景描述”选择服务商和模型。",
                "场景描述", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var completed = 0;
        var failed = 0;
        foreach (var plan in plans)
        {
            try
            {
                StatusText = $"正在描述“{directory.Name}”中的未分类场景：{completed + failed + 1}/{plans.Count}";
                var summary = await service.SummarizeWithAiAsync(plan, summarizer);
                if (summary is null) { failed++; continue; }
                await UiWork.RunDatabaseAsync(() => service.SaveAccepted(plan, summary));
                completed++;
            }
            catch (Exception ex)
            {
                failed++;
                AppLogger.Error($"批量场景描述失败：{plan.Fingerprint}", ex);
            }
        }
        await LoadSceneAnchorsAsync(directory, true);
        directory.IsExpanded = true;
        SelectedDirectory = directory;
        directory.IsSelected = true;
        StatusText = $"“{directory.Name}”场景描述完成：成功 {completed}，失败 {failed}，共 {plans.Count} 个";
        MessageBox.Show(StatusText, "批量场景描述", MessageBoxButton.OK,
            failed == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private static IEnumerable<DirectoryNodeViewModel> EnumerateDirectoryNodes(IEnumerable<DirectoryNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in EnumerateDirectoryNodes(node.Children)) yield return child;
        }
    }

    private async void LoadImagesForSelectedDirectory()
    {
        if (SelectedDirectory is null) return;
        var version = Interlocked.Increment(ref _directoryLoadVersion);
        var previousCancellation = _directoryLoadCts;
        previousCancellation?.Cancel();
        foreach (var item in DirectoryImages) item.CancelThumbnailLoad();
        previousCancellation?.Dispose();
        var cts = _directoryLoadCts = new CancellationTokenSource();
        var selectedDirectory = SelectedDirectory;
        bool IsCurrentLoad() => !cts.IsCancellationRequested && version == Volatile.Read(ref _directoryLoadVersion) &&
            ReferenceEquals(SelectedDirectory, selectedDirectory);
        DirectoryImages.Clear();
        DirectoryTimelineItems.Clear();
        PrepareDirectoryTimelineForLoading();
        try
        {
            if (selectedDirectory.IsAlbumRoot && selectedDirectory.AnchorImageIds.Count == 0)
            {
                OnPropertyChanged(nameof(DirectoryImageCount));
                StatusText = $"请选择“{selectedDirectory.Name}”下的一个相册";
                return;
            }
            var records = await UiWork.RunDatabaseAsync(() => selectedDirectory.IsSceneAnchor || selectedDirectory.IsGeoNode || selectedDirectory.IsAlbumRoot
                ? selectedDirectory.AnchorImageIds.Select(id => _db.GetImage(id)).Where(image => image is not null).Cast<ImageRecord>()
                    .OrderBy(SceneTimeClusterService.EffectiveTime).ThenBy(image => image.Id).ToList()
                : selectedDirectory.AlbumId is { } albumId
                    ? SemanticEngine.GetAlbumImages(_db, albumId)
                : _db.GetActiveImagesUnderDirectory(selectedDirectory.FullPath), cts.Token);
            if (!IsCurrentLoad()) return;
            records = (IsChronologicalAscending
                    ? records.OrderBy(SceneTimeClusterService.EffectiveTime).ThenBy(image => image.Id)
                    : records.OrderByDescending(SceneTimeClusterService.EffectiveTime).ThenByDescending(image => image.Id))
                .ToList();
            var indexedPaths = records.Select(record => record.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var photoItems = DirectoryMediaFilter == DirectoryMediaFilter.Videos
                ? []
                : await BuildBurstAwareItemsAsync(records.Where(image => image.Status == ImageStatus.Active).ToArray(), cts.Token);
            var videoItems = DirectoryMediaFilter == DirectoryMediaFilter.Images ||
                             selectedDirectory.IsSceneAnchor || selectedDirectory.IsGeoNode ||
                             selectedDirectory.IsAlbumRoot || selectedDirectory.AlbumId is not null
                ? []
                : await UiWork.RunDatabaseAsync("DirectoryVideos.Load", () =>
                {
                    // Keep the directory view self-healing: videos may have been imported
                    // before the explicit video scan was introduced. Index files under the
                    // selected source/child directory before reading the view.
                    var source = _db.GetSources().FirstOrDefault(candidate =>
                        string.Equals(Path.GetFullPath(candidate.Path).TrimEnd('\\', '/'),
                            Path.GetFullPath(selectedDirectory.FullPath).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
                        IsPathUnderDirectory(selectedDirectory.FullPath, candidate.Path));
                    if (source is not null)
                        AppServices.VideoIndex.ScanDirectory(source.Id, selectedDirectory.FullPath, true, cts.Token);
                    var posters = AppServices.Videos.GetFirstKeyframePaths();
                    return AppServices.Videos.GetAll()
                        .Where(video => video.Status == "active" && IsPathUnderDirectory(video.Path, selectedDirectory.FullPath))
                        .Where(video => string.IsNullOrWhiteSpace(SearchQuery) ||
                                        Path.GetFileName(video.Path).Contains(SearchQuery.Trim(), StringComparison.CurrentCultureIgnoreCase))
                        .Select(video => new ImageItemViewModel(video, posters.GetValueOrDefault(video.Id),
                            LoadVideoPosterPathAsync, cts.Token)).ToArray();
                }, cts.Token);
            var displayItems = photoItems.Concat(videoItems)
                .OrderBy(item => IsChronologicalAscending ? item.EffectiveTime.Ticks : -item.EffectiveTime.Ticks)
                .ThenBy(item => item.Id).ToArray();
            var displayed = 0;
            while (displayed < displayItems.Length)
            {
                cts.Token.ThrowIfCancellationRequested();
                if (!IsCurrentLoad()) return;
                // Paint roughly the first row immediately, then use larger batches so
                // large directories do not spend hundreds of dispatcher turns filling
                // view-model collections that are already visually virtualized.
                var batchSize = displayed == 0 ? 16 : 64;
                var batchEnd = Math.Min(displayItems.Length, displayed + batchSize);
                for (; displayed < batchEnd; displayed++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var item = displayItems[displayed];
                    item.SetTimelineGranularity(TimelineGranularityIndex);
                    DirectoryImages.Add(item);
                    AddTimelineItem(item);
                }
                if (displayed < displayItems.Length)
                    StatusText = $"[{selectedDirectory.Name}] 正在渐进显示：{displayed} / {displayItems.Length}";
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            if (!IsCurrentLoad()) return;
            RefreshDirectoryTimelineView(resetPosition: true);
            OnPropertyChanged(nameof(DirectoryImageCount));
            StatusText = $"[{selectedDirectory.Name}] 已显示 {DirectoryImages.Count} 个媒体项";

            var unindexedFiles = DirectoryMediaFilter == DirectoryMediaFilter.Videos || selectedDirectory.IsSceneAnchor || selectedDirectory.IsGeoNode || selectedDirectory.IsAlbumRoot || selectedDirectory.AlbumId is not null ? [] : await Task.Run(() =>
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
                };
                return Directory.EnumerateFiles(selectedDirectory.FullPath, "*", options)
                    .Where(path => { cts.Token.ThrowIfCancellationRequested(); return SupportedFormats.IsSupported(path); })
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

            if (!IsCurrentLoad()) return;
            await UiWork.AddInBatchesAsync(unindexedFiles,
                rec => { if (IsCurrentLoad()) DirectoryImages.Add(new ImageItemViewModel(rec, cts.Token)); }, cts.Token, batchSize: 8);

            if (!IsCurrentLoad()) return;

            if (selectedDirectory.IsSceneAnchor && DirectoryImages.Count > 0) SelectedImage = DirectoryImages[0];

            OnPropertyChanged(nameof(DirectoryImageCount));
            StatusText = $"[{SelectedDirectory.Name}] {DirectoryImages.Count} 张";
        }
        catch (OperationCanceledException) { }
        catch (UnauthorizedAccessException) { }
        catch (Exception exception) { StatusText = $"目录照片加载失败：{exception.Message}"; }
    }

    private static bool IsPathUnderDirectory(string path, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> LoadVideoPosterPathAsync(VideoRecord video, CancellationToken cancellationToken)
    {
        if (AppServices.VideoKeyframes is not { } keyframes) return null;
        await _videoPosterGate.WaitAsync(cancellationToken);
        try
        {
            return await keyframes.GetOrCreatePosterAsync(video, cancellationToken);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception exception) { AppLogger.Warn($"视频首帧生成失败：{video.Path}；{exception.Message}"); return null; }
        finally { _videoPosterGate.Release(); }
    }

    private void PrepareDirectoryTimelineForLoading()
    {
        _timelineAnchors = [];
        CurrentTimelineAnchor = "";
        TimelinePositionText = IsTimelineAlbumSelected ? "正在准备时间轴…" : "";
        CanNavigatePreviousTimeline = false;
        CanNavigateNextTimeline = false;
        DirectoryImagesView.GroupDescriptions.Clear();
        DirectoryImagesView.Filter = null;
    }

    private void RefreshDirectoryTimelineView(bool resetPosition = false)
    {
        foreach (var image in DirectoryImages) image.SetTimelineGranularity(TimelineGranularityIndex);
        _timelineAnchors = DirectoryImages.Select(image => image.TimelineAnchor)
            .Where(anchor => !string.IsNullOrWhiteSpace(anchor)).Distinct().ToArray();
        if (resetPosition || !_timelineAnchors.Contains(CurrentTimelineAnchor, StringComparer.Ordinal))
            CurrentTimelineAnchor = _timelineAnchors.FirstOrDefault() ?? "";
        DirectoryTimelineItems.Clear();
        foreach (var image in DirectoryImages) AddTimelineItem(image);
        using (DirectoryImagesView.DeferRefresh())
        {
            DirectoryImagesView.GroupDescriptions.Clear();
            DirectoryImagesView.Filter = null;
        }
        UpdateTimelineNavigationState();
        if (IsTimelineAlbumSelected)
            RequestTimelineScroll(CurrentTimelineAnchor);
    }

    [RelayCommand]
    private void NavigatePreviousTimeline() => NavigateTimeline(-1);

    [RelayCommand]
    private void NavigateNextTimeline() => NavigateTimeline(1);

    [RelayCommand] private void SetTimelineDay() => TimelineGranularityIndex = 0;
    [RelayCommand] private void SetTimelineWeek() => TimelineGranularityIndex = 1;
    [RelayCommand] private void SetTimelineMonth() => TimelineGranularityIndex = 2;
    [RelayCommand] private void SetTimelineYear() => TimelineGranularityIndex = 3;

    private void NavigateTimeline(int offset)
    {
        var current = Array.IndexOf(_timelineAnchors, CurrentTimelineAnchor);
        var target = current + offset;
        if (target < 0 || target >= _timelineAnchors.Length) return;
        CurrentTimelineAnchor = _timelineAnchors[target];
        RequestTimelineScroll(CurrentTimelineAnchor);
        UpdateTimelineNavigationState();
    }

    private void UpdateTimelineNavigationState()
    {
        var index = Array.IndexOf(_timelineAnchors, CurrentTimelineAnchor);
        TimelinePositionText = index < 0 ? "没有照片" : $"{index + 1} / {_timelineAnchors.Length}";
        CanNavigatePreviousTimeline = index > 0;
        CanNavigateNextTimeline = index >= 0 && index < _timelineAnchors.Length - 1;
    }

    private void AddTimelineItem(ImageItemViewModel image)
    {
        if (!IsTimelineAlbumSelected) return;
        var previous = DirectoryTimelineItems.LastOrDefault() as ImageItemViewModel;
        if (previous is null || previous.TimelineAnchor != image.TimelineAnchor)
            DirectoryTimelineItems.Add(new TimelineAnchorItemViewModel(image.TimelineAnchor));
        DirectoryTimelineItems.Add(image);
    }

    private void RequestTimelineScroll(string anchorLabel)
    {
        var target = DirectoryTimelineItems.OfType<TimelineAnchorItemViewModel>()
            .FirstOrDefault(anchor => anchor.Label == anchorLabel);
        TimelineScrollTarget = null;
        TimelineScrollTarget = target;
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
        _sceneContextCts?.Cancel();
        foreach (var item in DirectoryImages) item.CancelThumbnailLoad();
        _directoryLoadCts?.Dispose();
        _sceneContextCts?.Dispose();
        _indexCts?.Cancel();
        _cloudCts?.Cancel();
        _cloudCts?.Dispose();
        _locationEnrichmentCts.Dispose();
        People.Dispose();
    }
}

public sealed class AiVideoResultViewModel
{
    public AiVideoResultViewModel(VideoRecord record) => Record = record;
    public VideoRecord Record { get; }
    public long Id => Record.Id;
    public string FileName => Path.GetFileName(Record.Path);
    public string Detail => $"{(Record.TakenUtc ?? Record.ModifiedUtc):yyyy-MM-dd HH:mm} · {(Record.DurationSeconds is null ? "时长未知" : TimeSpan.FromSeconds(Record.DurationSeconds.Value).ToString(@"hh\:mm\:ss"))}";
}

public partial class DirectoryNodeViewModel : ObservableObject
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string Icon { get; set; } = "📁";
    public int ImageCount { get; set; }
    public bool IsLoaded { get; set; }
    public bool IsExpanded { get; set; }
    [ObservableProperty] private bool _isSelected;
    public bool IsSceneAnchor { get; set; }
    public bool IsAlbumRoot { get; set; }
    public long? AlbumId { get; set; }
    public bool IsTimelineAlbum { get; set; }
    public bool IsGeoNode { get; set; }
    public bool IsGeoRoot { get; set; }
    public bool IsPendingLocation { get; set; }
    internal List<long> MutableAnchorImageIds { get; } = [];
    public string SceneName { get; set; } = "";
    public bool SceneAnchorsLoaded { get; set; }
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ClassificationModeLabel))] private bool _classificationIncludesDescendants;
    public string ClassificationModeLabel => ClassificationIncludesDescendants ? "（含子目录）" : "";
    public IReadOnlyList<long> AnchorImageIds { get; set; } = [];
    public ObservableCollection<DirectoryNodeViewModel> Children { get; } = new();
}
