using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Picer.Core.Data;
using Picer.Core.People;
using Picer.Core.Vision;
using Picer.App.Views;
using System.Windows;
using Picer.App.Infrastructure;

namespace Picer.App.ViewModels;

public partial class PeopleViewModel : ObservableObject, IDisposable
{
    private readonly PicerDatabase _database;
    private readonly PeopleService _peopleService;
    private readonly VisionAnalysisRepository _repository;
    private readonly VisionAnalysisService? _analysisService;
    private readonly FaceThumbnailService _faceThumbnails;
    private readonly FaceSuggestionEngine _suggestionEngine;
    private readonly UnknownFaceClusterer _unknownFaceClusterer;
    private readonly string? _unavailableReason;
    private CancellationTokenSource? _analysisCancellation;
    private readonly SemaphoreSlim _analysisGate = new(1, 1);
    private CancellationTokenSource? _pageLoadCancellation;
    private CancellationTokenSource? _personLoadCancellation;
    private bool _isLoaded;

    public PeopleViewModel(PicerDatabase database, PeopleService peopleService,
        VisionAnalysisRepository repository, VisionAnalysisService? analysisService,
        FaceThumbnailService faceThumbnails, FaceSuggestionEngine suggestionEngine,
        UnknownFaceClusterer unknownFaceClusterer, string? unavailableReason)
    {
        _database = database;
        _peopleService = peopleService;
        _repository = repository;
        _analysisService = analysisService;
        _faceThumbnails = faceThumbnails;
        _suggestionEngine = suggestionEngine;
        _unknownFaceClusterer = unknownFaceClusterer;
        _unavailableReason = unavailableReason;
    }

    public ObservableCollection<PersonSummary> People { get; } = [];
    public ObservableCollection<UnassignedFaceViewModel> UnassignedFaces { get; } = [];
    public ObservableCollection<UnassignedFaceViewModel> AssignedFaces { get; } = [];
    public ObservableCollection<CandidateClusterViewModel> CandidateClusters { get; } = [];
    public ObservableCollection<UnassignedFaceViewModel> SelectedClusterFaces { get; } = [];
    public ObservableCollection<UnassignedFaceViewModel> DisplayedFaces { get; } = [];

    [ObservableProperty] private PersonSummary? _selectedPerson;
    [ObservableProperty] private UnassignedFaceViewModel? _selectedFace;
    [ObservableProperty] private CandidateClusterViewModel? _selectedCandidateCluster;
    [ObservableProperty] private UnassignedFaceViewModel? _selectedClusterFace;
    [ObservableProperty] private UnassignedFaceViewModel? _selectedDisplayedFace;
    [ObservableProperty] private string _displayedFacesTitle = "选择左侧分类查看人脸";
    [ObservableProperty] private string _newPersonName = "";
    [ObservableProperty] private string _statusText = "人物分析尚未开始";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelAnalysisCommand))]
    private bool _isAnalyzing;
    [ObservableProperty] private bool _isLoading;

    public bool IsModelAvailable => _analysisService is not null;
    public PersonRecord? FindByName(string name) => _peopleService.FindByName(name);
    public PersonQueryMention? FindMention(string query) => _peopleService.FindMention(query);

    public async Task InitializePageAsync()
    {
        if (_isLoaded || IsLoading) return;
        IsLoading = true;
        StatusText = "正在加载人物分类…";
        await RefreshAsync();
        _isLoaded = true;
        IsLoading = false;

        var summary = await Task.Run(_repository.GetSummary);
        if (summary.CompletedImages == 0 || IsAnalyzing) return;
        StatusText = "已显示人物分类，正在后台更新自动归类…";
        await OrganizeFacesAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _pageLoadCancellation?.Cancel();
        _pageLoadCancellation?.Dispose();
        var cancellation = _pageLoadCancellation = new CancellationTokenSource();
        var selectedPersonId = SelectedPerson?.Id;
        var snapshot = await Task.Run(() => (
            People: _peopleService.GetAll().ToArray(),
            Faces: _repository.GetUnassignedFaces().ToArray(),
            Clusters: _repository.GetCandidateClusters().ToArray(),
            Summary: _repository.GetSummary()), cancellation.Token);
        cancellation.Token.ThrowIfCancellationRequested();

        People.Clear();
        foreach (var person in snapshot.People) People.Add(person);
        SelectedPerson = selectedPersonId is long id ? People.FirstOrDefault(value => value.Id == id) : SelectedPerson;
        UnassignedFaces.Clear();
        await UiWork.AddInBatchesAsync(snapshot.Faces, face =>
            {
                var item = new UnassignedFaceViewModel(face);
                UnassignedFaces.Add(item);
                _ = LoadFaceThumbnailAsync(item);
            }, cancellation.Token);
        CandidateClusters.Clear();
        foreach (var cluster in snapshot.Clusters)
        {
            var item = new CandidateClusterViewModel(cluster);
            CandidateClusters.Add(item);
            _ = LoadClusterThumbnailAsync(item);
        }
        var summary = snapshot.Summary;
        StatusText = _unavailableReason ??
            $"已分析 {summary.CompletedImages}/{summary.TotalImages} 张照片，发现 {summary.FaceCount} 张人脸，失败 {summary.FailedImages} 张";
        ProgressValue = summary.TotalImages == 0 ? 0 : summary.CompletedImages * 100d / summary.TotalImages;
        RefreshDisplayedFaces();
    }

    [RelayCommand(CanExecute = nameof(CanAnalyzeAll))]
    private async Task AnalyzeAllAsync() => await ProcessNewPhotosAsync();

    public async Task ProcessNewPhotosAsync()
    {
        await _analysisGate.WaitAsync();
        if (_analysisService is null)
        {
            StatusText = _unavailableReason ?? "人脸模型不可用";
            _analysisGate.Release();
            return;
        }

        IsAnalyzing = true;
        _analysisCancellation = new CancellationTokenSource();
        try
        {
            var images = await Task.Run(() => _database.GetImages(includeMissing: false), _analysisCancellation.Token);
            var progress = new Progress<(int Completed, int Total)>(value =>
            {
                ProgressValue = value.Total == 0 ? 0 : value.Completed * 100d / value.Total;
                StatusText = $"正在分析人物：{value.Completed}/{value.Total}";
            });
            var result = await _analysisService.AnalyzeAsync(images, progress, _analysisCancellation.Token);
            await OrganizeFacesAsync(_analysisCancellation.Token);
            StatusText = $"人物分析和自动归类完成：新增 {result.Analyzed}，缓存跳过 {result.Skipped}，失败 {result.Failed}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "人物分析已取消，已完成结果仍然有效";
        }
        catch (Exception exception)
        {
            StatusText = $"人物自动归类失败：{exception.Message}";
        }
        finally
        {
            _analysisCancellation.Dispose();
            _analysisCancellation = null;
            IsAnalyzing = false;
            _analysisGate.Release();
        }
    }

    private bool CanAnalyzeAll() => !IsAnalyzing && _analysisService is not null;

    [RelayCommand(CanExecute = nameof(CanCancelAnalysis))]
    private void CancelAnalysis() => _analysisCancellation?.Cancel();

    private bool CanCancelAnalysis() => IsAnalyzing && _analysisCancellation is not null;

    [RelayCommand]
    private async Task CreatePersonAsync()
    {
        if (string.IsNullOrWhiteSpace(NewPersonName))
        {
            StatusText = "请输入人物名称";
            return;
        }
        try
        {
            var name = NewPersonName;
            var person = await Task.Run(() => _peopleService.Create(name));
            NewPersonName = "";
            await RefreshAsync();
            SelectedPerson = People.FirstOrDefault(value => value.Id == person.Id);
            StatusText = $"已创建人物“{person.DisplayName}”";
        }
        catch (Exception exception)
        {
            StatusText = $"创建人物失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private async Task OpenSelectedPersonDetailAsync()
    {
        if (SelectedPerson is null) { StatusText = "请先选择一个已命名人物"; return; }
        var window = new PersonDetailWindow(SelectedPerson.Id, _database, _peopleService, _repository)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task AssignSelectedFaceAsync()
    {
        if (SelectedFace is null || (SelectedPerson is null && SelectedFace.Record.SuggestedPersonId is null))
        {
            StatusText = "请先选择一张未归类人脸和一个人物";
            return;
        }
        var personId = SelectedPerson?.Id ?? SelectedFace.Record.SuggestedPersonId!.Value;
        var personName = SelectedPerson?.DisplayName ?? SelectedFace.Record.SuggestedPersonName!;
        await Task.Run(() => _peopleService.ConfirmFace(SelectedFace.Record.FaceId, personId));
        await RefreshAsync();
        StatusText = $"已将所选人脸确认为“{personName}”";
    }

    [RelayCommand]
    private async Task SuggestMatchesAsync()
    {
        StatusText = "正在匹配已命名人物…";
        var result = await Task.Run(() => _suggestionEngine.Suggest());
        await RefreshAsync();
        StatusText = $"候选匹配完成：检查 {result.Considered} 张脸，新增建议 {result.Suggested}，低于阈值 {result.BelowThreshold}；使用阈值 {result.AppliedThreshold:F3}";
    }

    private async Task OrganizeFacesAsync(CancellationToken cancellationToken = default)
    {
        var suggestions = await Task.Run(() => _suggestionEngine.Suggest(), cancellationToken);
        var clusters = await Task.Run(() => _unknownFaceClusterer.Rebuild(), cancellationToken);
        await RefreshAsync();
        StatusText = $"自动归类完成：匹配已有分类 {suggestions.Suggested} 张，待命名 {clusters.ClusterCount} 组";
    }

    [RelayCommand]
    private async Task RejectSuggestionAsync()
    {
        if (SelectedFace?.Record.SuggestedPersonId is not long personId)
        {
            StatusText = "所选人脸没有待确认建议";
            return;
        }
        await Task.Run(() => _peopleService.MarkNotThisPerson(SelectedFace.Record.FaceId, personId));
        await RefreshAsync();
        StatusText = "已标记为不是此人，后续自动匹配不会再次给出该建议";
    }

    [RelayCommand]
    private async Task OpenSelectedFacePhotoAsync()
    {
        await OpenFacePhotoAsync(SelectedDisplayedFace ?? SelectedFace ?? SelectedClusterFace);
    }

    public async Task OpenFacePhotoAsync(UnassignedFaceViewModel? face)
    {
        if (face is null)
        {
            StatusText = "请先选择一张人脸";
            return;
        }
        var image = await Task.Run(() => _database.GetImage(face.Record.ImageId));
        if (image is null || !File.Exists(image.Path))
        {
            StatusText = "原照片当前不可用";
            return;
        }
        var window = new FaceDetailWindow(image, _repository, _peopleService, face.FaceId)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
        await RefreshAsync();
    }

    public async Task MoveFaceToPersonAsync(UnassignedFaceViewModel face, PersonSummary person)
    {
        await Task.Run(() => _peopleService.MoveFace(face.FaceId, person.Id));
        StatusText = $"已将人脸移到“{person.DisplayName}”";
        await RefreshAsync();
    }

    public async Task MoveFaceToUnassignedAsync(UnassignedFaceViewModel face)
    {
        await Task.Run(() => _peopleService.MoveFaceToUnassigned(face.FaceId));
        StatusText = "已移到待分类人脸";
        await RefreshAsync();
    }

    public async Task IgnoreFaceAsync(UnassignedFaceViewModel face)
    {
        await Task.Run(() => _peopleService.IgnoreFace(face.FaceId));
        StatusText = "已标记为不分类；后续自动归类会跳过这张脸";
        await RefreshAsync();
    }

    [RelayCommand]
    private void ShowUnassignedFaces()
    {
        SelectedPerson = null;
        SelectedCandidateCluster = null;
        DisplayedFacesTitle = $"待分类人脸（{UnassignedFaces.Count}）";
        SetDisplayedFaces(UnassignedFaces);
    }

    [RelayCommand]
    private async Task ShowIgnoredFacesAsync()
    {
        SelectedPerson = null;
        SelectedCandidateCluster = null;
        var records = await Task.Run(() => _repository.GetIgnoredFaces().ToArray());
        var faces = records.Select(face => new UnassignedFaceViewModel(face)).ToArray();
        DisplayedFacesTitle = $"不分类（{faces.Length}）";
        SetDisplayedFaces(faces);
    }

    [RelayCommand]
    private async Task BuildCandidateClustersAsync()
    {
        StatusText = "正在分析陌生人物候选组…";
        var result = await Task.Run(() => _unknownFaceClusterer.Rebuild());
        await RefreshAsync();
        StatusText = $"候选分组完成：{result.ClusterCount} 组、{result.ClusteredFaces} 张脸，未成组 {result.Considered - result.ClusteredFaces} 张";
    }

    [RelayCommand]
    private async Task NameCandidateClusterAsync()
    {
        if (SelectedCandidateCluster is null || string.IsNullOrWhiteSpace(NewPersonName))
        {
            StatusText = "请选择候选组并输入人物名称";
            return;
        }
        var name = NewPersonName;
        var faces = SelectedCandidateCluster.Record.Faces.ToArray();
        var person = await Task.Run(() =>
        {
            var created = _peopleService.Create(name);
            foreach (var face in faces) _peopleService.ConfirmFace(face.FaceId, created.Id);
            return created;
        });
        NewPersonName = "";
        await RefreshAsync();
        StatusText = $"已将候选组确认为“{person.DisplayName}”，可继续逐脸纠错";
    }

    partial void OnSelectedCandidateClusterChanged(CandidateClusterViewModel? value)
    {
        SelectedClusterFaces.Clear();
        if (value is null) return;
        foreach (var face in value.Record.Faces)
        {
            var item = new UnassignedFaceViewModel(face);
            SelectedClusterFaces.Add(item);
            _ = LoadClusterMemberThumbnailAsync(item);
        }
        if (value is not null)
        {
            SelectedPerson = null;
            DisplayedFacesTitle = $"{value.Summary}（可输入名称后确认整组）";
            SetDisplayedFaces(SelectedClusterFaces);
        }
    }

    [RelayCommand]
    private async Task RemoveSelectedFaceFromClusterAsync()
    {
        if (SelectedCandidateCluster is null || SelectedClusterFace is null)
        { StatusText = "请先选择候选组中的一张错误人脸"; return; }
        await Task.Run(() => _repository.RemoveFaceFromCandidateCluster(SelectedCandidateCluster.Record.Id, SelectedClusterFace.FaceId));
        StatusText = "已从候选组拆出该人脸；人脸仍保留在未归类列表中";
        await RefreshAsync();
    }

    public void Dispose()
    {
        _analysisCancellation?.Cancel();
        _analysisCancellation?.Dispose();
        _pageLoadCancellation?.Cancel();
        _pageLoadCancellation?.Dispose();
        _personLoadCancellation?.Cancel();
        _personLoadCancellation?.Dispose();
    }

    private async Task LoadFaceThumbnailAsync(UnassignedFaceViewModel item)
    {
        var path = await _faceThumbnails.GetOrCreateAsync(item.Record);
        if (path is not null && UnassignedFaces.Contains(item)) item.FaceThumbnailPath = path;
    }


    private async Task LoadClusterThumbnailAsync(CandidateClusterViewModel item)
    {
        var representative = item.Record.Faces.FirstOrDefault();
        if (representative is null) return;
        var path = await _faceThumbnails.GetOrCreateAsync(representative);
        if (path is not null && CandidateClusters.Contains(item)) item.RepresentativeThumbnailPath = path;
    }

    private async Task LoadClusterMemberThumbnailAsync(UnassignedFaceViewModel item)
    {
        var path = await _faceThumbnails.GetOrCreateAsync(item.Record);
        if (path is not null && SelectedClusterFaces.Contains(item)) item.FaceThumbnailPath = path;
    }

    partial void OnSelectedPersonChanged(PersonSummary? value)
    {
        AssignedFaces.Clear();
        if (value is null) return;
        _personLoadCancellation?.Cancel();
        _personLoadCancellation?.Dispose();
        var cancellation = _personLoadCancellation = new CancellationTokenSource();
        SelectedCandidateCluster = null;
        DisplayedFacesTitle = $"{value.DisplayName} · 正在加载…";
        DisplayedFaces.Clear();
        _ = LoadPersonFacesAsync(value, cancellation.Token);
    }

    private async Task LoadPersonFacesAsync(PersonSummary person, CancellationToken cancellationToken)
    {
        try
        {
            var faces = await Task.Run(() => _repository.GetPersonFaces(person.Id).ToArray(), cancellationToken);
            if (cancellationToken.IsCancellationRequested || SelectedPerson?.Id != person.Id) return;
            await UiWork.AddInBatchesAsync(faces, face =>
                {
                    var item = new UnassignedFaceViewModel(face);
                    AssignedFaces.Add(item);
                    DisplayedFaces.Add(item);
                    _ = LoadAssignedFaceThumbnailAsync(item);
                    _ = LoadDisplayedFaceThumbnailAsync(item);
                DisplayedFacesTitle = $"{person.DisplayName} · {AssignedFaces.Count} / {faces.Length} 张人脸";
                }, cancellationToken);
            DisplayedFacesTitle = $"{person.DisplayName} · {AssignedFaces.Count} 张人脸";
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadAssignedFaceThumbnailAsync(UnassignedFaceViewModel item)
    {
        var path = await _faceThumbnails.GetOrCreateAsync(item.Record);
        if (path is not null && AssignedFaces.Contains(item)) item.FaceThumbnailPath = path;
    }

    private void RefreshDisplayedFaces()
    {
        if (SelectedPerson is not null) OnSelectedPersonChanged(SelectedPerson);
        else if (SelectedCandidateCluster is not null) OnSelectedCandidateClusterChanged(SelectedCandidateCluster);
        else if (DisplayedFacesTitle.StartsWith("不分类", StringComparison.Ordinal)) _ = ShowIgnoredFacesAsync();
        else ShowUnassignedFaces();
    }

    private void SetDisplayedFaces(IEnumerable<UnassignedFaceViewModel> faces)
    {
        DisplayedFaces.Clear();
        foreach (var face in faces)
        {
            DisplayedFaces.Add(face);
            _ = LoadDisplayedFaceThumbnailAsync(face);
        }
    }

    private async Task LoadDisplayedFaceThumbnailAsync(UnassignedFaceViewModel item)
    {
        var path = await _faceThumbnails.GetOrCreateAsync(item.Record);
        if (path is not null && DisplayedFaces.Contains(item)) item.FaceThumbnailPath = path;
    }
}

public partial class UnassignedFaceViewModel(UnassignedFaceRecord record) : ObservableObject
{
    public UnassignedFaceRecord Record { get; } = record;
    public long FaceId => Record.FaceId;
    public string ImagePath => Record.ImagePath;
    public int Ordinal => Record.Ordinal + 1;
    public float Confidence => Record.Confidence;
    public string SuggestedText => Record.SuggestedPersonName is null ? "未归类" : $"可能是：{Record.SuggestedPersonName}";

    [ObservableProperty]
    private string? _faceThumbnailPath = record.ThumbnailPath ?? record.ImagePath;
}

public partial class CandidateClusterViewModel(FaceCandidateClusterRecord record) : ObservableObject
{
    public FaceCandidateClusterRecord Record { get; } = record;
    public int FaceCount => Record.Faces.Count;
    public string Summary => $"候选人物 · {FaceCount} 张脸";

    [ObservableProperty]
    private string? _representativeThumbnailPath = record.Faces.FirstOrDefault()?.ThumbnailPath
        ?? record.Faces.FirstOrDefault()?.ImagePath;
}
