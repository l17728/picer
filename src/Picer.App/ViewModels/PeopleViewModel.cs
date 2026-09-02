using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Picer.Core.Data;
using Picer.Core.People;
using Picer.Core.Logging;
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
    private readonly SemaphoreSlim _thumbnailGate = new(6, 6);
    private CancellationTokenSource? _pageLoadCancellation;
    private CancellationTokenSource? _personLoadCancellation;
    private CancellationTokenSource? _clusterLoadCancellation;
    private CancellationTokenSource? _collectionLoadCancellation;
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
    [ObservableProperty] private PersonSummary? _candidateTargetPerson;
    [ObservableProperty] private UnassignedFaceViewModel? _selectedFace;
    [ObservableProperty] private CandidateClusterViewModel? _selectedCandidateCluster;
    [ObservableProperty] private UnassignedFaceViewModel? _selectedClusterFace;
    [ObservableProperty] private UnassignedFaceViewModel? _selectedDisplayedFace;
    [ObservableProperty] private string _displayedFacesTitle = "选择左侧分类查看人脸";
    public DisplayedFaceCollectionKind DisplayedCollectionKind { get; private set; }
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

        var summary = await UiWork.RunDatabaseAsync(_repository.GetSummary);
        if (summary.CompletedImages == 0 || IsAnalyzing) return;
        StatusText = "已显示人物分类，正在后台更新自动归类…";
        await OrganizeFacesAsync();
    }

    private async Task RefreshAsync()
    {
        _pageLoadCancellation?.Cancel();
        _personLoadCancellation?.Cancel();
        _clusterLoadCancellation?.Cancel();
        _collectionLoadCancellation?.Cancel();
        var cancellation = _pageLoadCancellation = new CancellationTokenSource();
        var selectedPersonId = SelectedPerson?.Id;
        var selectedClusterId = SelectedCandidateCluster?.Record.Id;
        var restoreIgnored = DisplayedFacesTitle.StartsWith("不分类", StringComparison.Ordinal);
        var restoreUnassigned = DisplayedFacesTitle.StartsWith("待分类", StringComparison.Ordinal);
        var snapshot = await UiWork.RunDatabaseAsync(() => (
            People: _peopleService.GetAll().ToArray(),
            Faces: _repository.GetUnassignedFaces().ToArray(),
            Clusters: _repository.GetCandidateClusters().ToArray(),
            IgnoredCount: _repository.GetIgnoredFaceCount(),
            Summary: _repository.GetSummary()), cancellation.Token);
        cancellation.Token.ThrowIfCancellationRequested();

        SelectedPerson = null;
        SelectedCandidateCluster = null;
        SelectedFace = null;
        SelectedClusterFace = null;
        SelectedDisplayedFace = null;
        DisplayedFaces.Clear();
        AssignedFaces.Clear();
        SelectedClusterFaces.Clear();
        People.Clear();
        foreach (var person in snapshot.People) People.Add(person);
        UnassignedFaces.Clear();
        await UiWork.AddInBatchesAsync(snapshot.Faces, face =>
            {
                var item = CreateFaceItem(face);
                UnassignedFaces.Add(item);
                _ = LoadFaceThumbnailAsync(item);
            }, cancellation.Token);
        CandidateClusters.Clear();
        foreach (var cluster in snapshot.Clusters)
        {
            var item = new CandidateClusterViewModel(cluster)
            {
                RepresentativeThumbnailPath = cluster.Faces.FirstOrDefault() is { } representative
                    ? _faceThumbnails.GetCachedPath(representative.FaceId)
                    : null
            };
            CandidateClusters.Add(item);
            _ = LoadClusterThumbnailAsync(item);
        }
        var summary = snapshot.Summary;
        StatusText = _unavailableReason ??
            $"已分析 {summary.CompletedImages}/{summary.TotalImages} 张照片，发现 {summary.FaceCount} 张人脸，失败 {summary.FailedImages} 张";
        ProgressValue = summary.TotalImages == 0 ? 0 : summary.CompletedImages * 100d / summary.TotalImages;
        var restoredPerson = selectedPersonId is long personId ? People.FirstOrDefault(value => value.Id == personId) : null;
        var restoredCluster = selectedClusterId is long clusterId ? CandidateClusters.FirstOrDefault(value => value.Record.Id == clusterId) : null;
        if (restoredPerson is not null) SelectedPerson = restoredPerson;
        else if (restoredCluster is not null) SelectedCandidateCluster = restoredCluster;
        else if (restoreIgnored) await ShowIgnoredFacesAsync();
        else if (restoreUnassigned || DisplayedFaces.Count == 0) ShowUnassignedFaces();
    }

    [RelayCommand]
    private async Task RefreshPageAsync()
    {
        StatusText = "正在同步人物界面…";
        await RefreshAsync();
        var ignored = await UiWork.RunDatabaseAsync(_repository.GetIgnoredFaceCount);
        StatusText = $"UI 已同步：{People.Count} 位人物，{CandidateClusters.Count} 个候选组，" +
            $"{UnassignedFaces.Count} 张待分类，{ignored} 张不分类";
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
            var images = await UiWork.RunDatabaseAsync(() => _database.GetImages(includeMissing: false), _analysisCancellation.Token);
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
            var person = await UiWork.RunDatabaseAsync(() => _peopleService.Create(name));
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
        await UiWork.RunDatabaseAsync(() => _peopleService.ConfirmFace(SelectedFace.Record.FaceId, personId));
        await RefreshAsync();
        StatusText = $"已将所选人脸确认为“{personName}”";
    }

    [RelayCommand]
    private async Task SuggestMatchesAsync()
    {
        StatusText = "正在匹配已命名人物…";
        var result = await UiWork.RunDatabaseAsync(() => _suggestionEngine.Suggest());
        await RefreshAsync();
        StatusText = $"候选匹配完成：检查 {result.Considered} 张脸，新增建议 {result.Suggested}，低于阈值 {result.BelowThreshold}；使用阈值 {result.AppliedThreshold:F3}";
    }

    [RelayCommand]
    private async Task OrganizeFacesAsync(CancellationToken cancellationToken = default)
    {
        StatusText = "正在使用最新标准更新自动归类…";
        var suggestions = await UiWork.RunDatabaseAsync(() => _suggestionEngine.Suggest(), cancellationToken);
        var clusters = await UiWork.RunDatabaseAsync(
            () => _unknownFaceClusterer.Rebuild(suggestions.AppliedThreshold), cancellationToken);
        await RefreshAsync();
        StatusText = $"自动归类完成：使用阈值 {suggestions.AppliedThreshold:F3}，匹配已有分类 {suggestions.Suggested} 张，待命名 {clusters.ClusterCount} 组";
    }

    [RelayCommand]
    private async Task RejectSuggestionAsync()
    {
        if (SelectedFace?.Record.SuggestedPersonId is not long personId)
        {
            StatusText = "所选人脸没有待确认建议";
            return;
        }
        await UiWork.RunDatabaseAsync(() => _peopleService.MarkNotThisPerson(SelectedFace.Record.FaceId, personId));
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
        var image = await UiWork.RunDatabaseAsync(() => _database.GetImage(face.Record.ImageId));
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
        => await MoveFacesToPersonAsync([face], person);

    public async Task MoveFacesToPersonAsync(IReadOnlyCollection<UnassignedFaceViewModel> faces, PersonSummary person)
    {
        var faceIds = faces.Select(face => face.FaceId).Distinct().ToArray();
        if (faceIds.Length == 0) return;
        await UiWork.RunDatabaseAsync(() =>
        {
            foreach (var faceId in faceIds) _peopleService.MoveFace(faceId, person.Id);
        });
        await RefreshAsync();
        StatusText = $"已将 {faceIds.Length} 张人脸移到“{person.DisplayName}”";
    }

    public async Task MoveFaceToUnassignedAsync(UnassignedFaceViewModel face)
        => await MoveFacesToUnassignedAsync([face]);

    public async Task MoveFacesToUnassignedAsync(IReadOnlyCollection<UnassignedFaceViewModel> faces)
    {
        var faceIds = faces.Select(face => face.FaceId).Distinct().ToArray();
        if (faceIds.Length == 0) return;
        await UiWork.RunDatabaseAsync(() =>
        {
            foreach (var faceId in faceIds) _peopleService.MoveFaceToUnassigned(faceId);
        });
        await RefreshAsync();
        StatusText = $"已将 {faceIds.Length} 张人脸移到待分类";
    }

    public async Task IgnoreFaceAsync(UnassignedFaceViewModel face)
        => await IgnoreFacesAsync([face]);

    public async Task IgnoreFacesAsync(IReadOnlyCollection<UnassignedFaceViewModel> faces)
    {
        var faceIds = faces.Select(face => face.FaceId).Distinct().ToArray();
        if (faceIds.Length == 0) return;
        StatusText = $"正在将 {faceIds.Length} 张人脸移到不分类…";
        var changed = 0;
        try
        {
            changed = await UiWork.RunDatabaseAsync(() =>
            {
                var count = 0;
                foreach (var faceId in faceIds)
                    if (_peopleService.IgnoreFace(faceId)) count++;
                return count;
            });
            var verified = await UiWork.RunDatabaseAsync(() =>
                _repository.GetIgnoredFaces(5000).Select(face => face.FaceId).ToHashSet());
            var missing = faceIds.Where(faceId => !verified.Contains(faceId)).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException($"数据库复核失败，仍有 {missing.Length} 张人脸未进入不分类");
            foreach (var item in DisplayedFaces.Where(item => faceIds.Contains(item.FaceId)).ToArray())
                DisplayedFaces.Remove(item);
            await RefreshAsync();
            StatusText = changed == 0
                ? $"所选 {faceIds.Length} 张人脸早已是不分类；界面已重新同步"
                : $"已将 {changed} 张人脸移到不分类；后续自动归类会跳过";
        }
        catch (Exception exception)
        {
            AppLogger.Error($"移动人脸到不分类失败：{string.Join(',', faceIds)}", exception);
            var alreadyIgnored = await UiWork.RunDatabaseAsync(() =>
                _repository.GetIgnoredFaces(5000).Select(face => face.FaceId).ToHashSet());
            if (faceIds.All(alreadyIgnored.Contains))
            {
                foreach (var item in DisplayedFaces.Where(item => faceIds.Contains(item.FaceId)).ToArray())
                    DisplayedFaces.Remove(item);
                await RefreshAsync();
                StatusText = $"所选 {faceIds.Length} 张人脸已是不分类；界面已重新同步";
            }
            else StatusText = $"移动到不分类失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private void ShowUnassignedFaces()
    {
        _collectionLoadCancellation?.Cancel();
        SelectedPerson = null;
        SelectedCandidateCluster = null;
        DisplayedCollectionKind = DisplayedFaceCollectionKind.Unassigned;
        DisplayedFacesTitle = $"待分类人脸（{UnassignedFaces.Count}）";
        SetDisplayedFaces(UnassignedFaces);
    }

    [RelayCommand]
    private async Task ShowIgnoredFacesAsync()
    {
        _collectionLoadCancellation?.Cancel();
        _collectionLoadCancellation?.Dispose();
        SelectedPerson = null;
        SelectedCandidateCluster = null;
        DisplayedCollectionKind = DisplayedFaceCollectionKind.Ignored;
        DisplayedFaces.Clear();
        DisplayedFacesTitle = "不分类 · 正在加载…";
        StatusText = "正在加载不分类人脸…";
        var cancellation = _collectionLoadCancellation = new CancellationTokenSource();
        try
        {
            var records = await UiWork.RunDatabaseAsync(() => _repository.GetIgnoredFaces(5000).ToArray(), cancellation.Token);
            await UiWork.AddInBatchesAsync(records, record =>
            {
                var item = CreateFaceItem(record);
                DisplayedFaces.Add(item);
                LoadDisplayedFaceThumbnailAsync(item, cancellation.Token).Forget();
                DisplayedFacesTitle = $"不分类 · {DisplayedFaces.Count}/{records.Length} 张人脸";
            }, cancellation.Token, batchSize: 30);
            DisplayedFacesTitle = $"不分类（{records.Length}）";
            StatusText = records.Length == 0 ? "当前没有标记为不分类的人脸" : $"已显示 {records.Length} 张不分类人脸";
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    private async Task BuildCandidateClustersAsync()
    {
        StatusText = "正在分析陌生人物候选组…";
        var result = await UiWork.RunDatabaseAsync(() => _unknownFaceClusterer.Rebuild());
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
        var person = await UiWork.RunDatabaseAsync(() =>
        {
            var created = _peopleService.Create(name);
            foreach (var face in faces) _peopleService.ConfirmFace(face.FaceId, created.Id);
            return created;
        });
        NewPersonName = "";
        await RefreshAsync();
        StatusText = $"已将候选组确认为“{person.DisplayName}”，可继续逐脸纠错";
    }

    [RelayCommand]
    private async Task AssignCandidateClusterToSelectedPersonAsync()
    {
        if (SelectedCandidateCluster is null || CandidateTargetPerson is null)
        {
            StatusText = "请先选择一个待命名人物组和目标已命名人物";
            return;
        }
        var person = CandidateTargetPerson;
        var faceIds = SelectedCandidateCluster.Record.Faces.Select(face => face.FaceId).Distinct().ToArray();
        await UiWork.RunDatabaseAsync(() =>
        {
            foreach (var faceId in faceIds) _peopleService.MoveFace(faceId, person.Id);
        });
        await RefreshAsync();
        SelectedPerson = People.FirstOrDefault(value => value.Id == person.Id);
        StatusText = $"已将候选组中的 {faceIds.Length} 张人脸归入“{person.DisplayName}”";
    }

    partial void OnSelectedCandidateClusterChanged(CandidateClusterViewModel? value)
    {
        _collectionLoadCancellation?.Cancel();
        _clusterLoadCancellation?.Cancel();
        SelectedClusterFaces.Clear();
        if (value is null) return;
        var cancellation = _clusterLoadCancellation = new CancellationTokenSource();
        SelectedPerson = null;
        DisplayedCollectionKind = DisplayedFaceCollectionKind.CandidateCluster;
        DisplayedFaces.Clear();
        DisplayedFacesTitle = $"{value.Summary} · 正在加载…";
        _ = LoadCandidateClusterAsync(value, cancellation.Token);
    }

    private async Task LoadCandidateClusterAsync(CandidateClusterViewModel cluster, CancellationToken cancellationToken)
    {
        try
        {
            var faces = cluster.Record.Faces.ToArray();
            await UiWork.AddInBatchesAsync(faces, face =>
            {
                var item = CreateFaceItem(face);
                SelectedClusterFaces.Add(item);
                DisplayedFaces.Add(item);
                LoadDisplayedFaceThumbnailAsync(item, cancellationToken).Forget();
                DisplayedFacesTitle = $"候选人物 · {DisplayedFaces.Count} / {faces.Length} 张脸";
            }, cancellationToken);
            DisplayedFacesTitle = $"{cluster.Summary}（可输入名称后确认整组）";
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    private async Task RemoveSelectedFaceFromClusterAsync()
    {
        if (SelectedCandidateCluster is null || SelectedClusterFace is null)
        { StatusText = "请先选择候选组中的一张错误人脸"; return; }
        await UiWork.RunDatabaseAsync(() => _repository.RemoveFaceFromCandidateCluster(SelectedCandidateCluster.Record.Id, SelectedClusterFace.FaceId));
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
        _clusterLoadCancellation?.Cancel();
        _clusterLoadCancellation?.Dispose();
        _collectionLoadCancellation?.Cancel();
        _collectionLoadCancellation?.Dispose();
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
        _collectionLoadCancellation?.Cancel();
        _personLoadCancellation?.Cancel();
        _personLoadCancellation?.Dispose();
        AssignedFaces.Clear();
        if (value is null) return;
        var cancellation = _personLoadCancellation = new CancellationTokenSource();
        SelectedCandidateCluster = null;
        DisplayedCollectionKind = DisplayedFaceCollectionKind.NamedPerson;
        DisplayedFacesTitle = $"{value.DisplayName} · 正在加载…";
        DisplayedFaces.Clear();
        _ = LoadPersonFacesAsync(value, cancellation.Token);
    }

    private async Task LoadPersonFacesAsync(PersonSummary person, CancellationToken cancellationToken)
    {
        try
        {
            var faces = await UiWork.RunDatabaseAsync(() => _repository.GetPersonFaces(person.Id).ToArray(), cancellationToken);
            if (cancellationToken.IsCancellationRequested || SelectedPerson?.Id != person.Id) return;
            await UiWork.AddInBatchesAsync(faces, face =>
                {
                    var item = CreateFaceItem(face);
                    AssignedFaces.Add(item);
                    DisplayedFaces.Add(item);
                    LoadDisplayedFaceThumbnailAsync(item, cancellationToken).Forget();
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

    private UnassignedFaceViewModel CreateFaceItem(UnassignedFaceRecord record) =>
        new(record, _faceThumbnails.GetCachedPath(record.FaceId) ?? GetSafePreviewPath(record));

    private static string? GetSafePreviewPath(UnassignedFaceRecord record) =>
        !string.IsNullOrWhiteSpace(record.ThumbnailPath) && File.Exists(record.ThumbnailPath)
            ? record.ThumbnailPath
            : null;

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

    private async Task LoadDisplayedFaceThumbnailAsync(UnassignedFaceViewModel item, CancellationToken cancellationToken = default)
    {
        await _thumbnailGate.WaitAsync(cancellationToken);
        try
        {
            if (!DisplayedFaces.Contains(item)) return;
            var path = await _faceThumbnails.GetOrCreateAsync(item.Record);
            cancellationToken.ThrowIfCancellationRequested();
            if (path is not null && DisplayedFaces.Contains(item)) item.FaceThumbnailPath = path;
        }
        finally { _thumbnailGate.Release(); }
    }
}

public enum DisplayedFaceCollectionKind
{
    None,
    NamedPerson,
    CandidateCluster,
    Unassigned,
    Ignored
}

public partial class UnassignedFaceViewModel(UnassignedFaceRecord record, string? initialThumbnailPath = null) : ObservableObject
{
    public UnassignedFaceRecord Record { get; } = record;
    public long FaceId => Record.FaceId;
    public string ImagePath => Record.ImagePath;
    public int Ordinal => Record.Ordinal + 1;
    public float Confidence => Record.Confidence;
    public string SuggestedText => Record.SuggestedPersonName is null ? "未归类" : $"可能是：{Record.SuggestedPersonName}";

    [ObservableProperty]
    private string? _faceThumbnailPath = initialThumbnailPath;
}

public partial class CandidateClusterViewModel(FaceCandidateClusterRecord record) : ObservableObject
{
    public FaceCandidateClusterRecord Record { get; } = record;
    public int FaceCount => Record.Faces.Count;
    public string Summary => $"候选人物 · {FaceCount} 张脸";

    [ObservableProperty]
    private string? _representativeThumbnailPath;
}
