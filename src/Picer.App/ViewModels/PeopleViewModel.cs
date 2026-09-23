using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Picer.Core.Data;
using Picer.Core.People;
using Picer.Core.Logging;
using Picer.Core.Vision;
using Picer.Core.Gateway;
using Picer.Core.Indexing;
using Picer.App.Views;
using System.Windows;
using Picer.App.Infrastructure;
using Picer.App.Security;

namespace Picer.App.ViewModels;

public sealed record FaceModelOption(string ModelVersion, string DisplayName);
public sealed record FaceTrainingHistoryOption(string ArtifactId, string ModelVersion, double ValidationAccuracy, DateTime CreatedUtc, string State, bool CanApply = true)
{
    public string DisplayName => $"{CreatedUtc:yyyy-MM-dd HH:mm} · {ValidationAccuracy:P1} · {State} · {ArtifactId[..Math.Min(12, ArtifactId.Length)]}" +
        (CanApply ? "" : " · 平台历史指标（制品不可用）");
}

public partial class PeopleViewModel : ObservableObject, IDisposable
{
    private static long _faceMoveOperationId;
    private readonly PicerDatabase _database;
    private readonly PeopleService _peopleService;
    private readonly VisionAnalysisRepository _repository;
    private readonly VisionAnalysisService? _analysisService;
    private readonly FaceThumbnailService _faceThumbnails;
    private readonly IMediaAssetGateway? _mediaGateway;
    private readonly FaceSuggestionEngine _suggestionEngine;
    private readonly UnknownFaceClusterer _unknownFaceClusterer;
    private readonly string? _unavailableReason;
    private CancellationTokenSource? _analysisCancellation;
    private readonly SemaphoreSlim _analysisGate = new(1, 1);
    private readonly SemaphoreSlim _organizeGate = new(1, 1);
    private readonly SemaphoreSlim _thumbnailGate = new(6, 6);
    private CancellationTokenSource? _pageLoadCancellation;
    private CancellationTokenSource? _personLoadCancellation;
    private CancellationTokenSource? _clusterLoadCancellation;
    private CancellationTokenSource? _collectionLoadCancellation;
    private string? _lastOrganizeFingerprint;
    private bool _isLoaded;
    private bool _suppressPersonSelectionLoad;
    private bool _suppressCollectionSelectionCallbacks;
    private int _baselinePublishStarted;
    private readonly SemaphoreSlim _modelSwitchGate = new(1, 1);
    private CancellationTokenSource? _modelSwitchCancellation;
    private CancellationTokenSource? _organizeCancellation;
    private long _modelSwitchGeneration;
    private long _personToRestoreAfterModelSwitch;

    public PeopleViewModel(PicerDatabase database, PeopleService peopleService,
        VisionAnalysisRepository repository, VisionAnalysisService? analysisService,
        FaceThumbnailService faceThumbnails, FaceSuggestionEngine suggestionEngine,
        UnknownFaceClusterer unknownFaceClusterer, string? unavailableReason, IMediaAssetGateway? mediaGateway = null)
    {
        _database = database;
        _peopleService = peopleService;
        _repository = repository;
        _analysisService = analysisService;
        _faceThumbnails = faceThumbnails;
        _mediaGateway = mediaGateway;
        _suggestionEngine = suggestionEngine;
        _unknownFaceClusterer = unknownFaceClusterer;
        _unavailableReason = unavailableReason;
    }

    private async Task<string?> GetFaceThumbnailAsync(UnassignedFaceRecord face, CancellationToken cancellationToken = default)
    {
        if (_mediaGateway is not null && AppServices.ServiceMode)
        {
            await _thumbnailGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var cached = _faceThumbnails.GetCachedPath(face.FaceId);
                if (cached is not null) return cached;
                await using var stream = await _mediaGateway.OpenFaceThumbnailAsync(face.FaceId, cancellationToken: cancellationToken).ConfigureAwait(false);
                return stream is null ? null : await _faceThumbnails.StoreAsync(face.FaceId, stream, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidOperationException)
            {
                AppLogger.Warn($"Remote face thumbnail failed face={face.FaceId}: {exception.Message}");
                return null;
            }
            finally { _thumbnailGate.Release(); }
        }
        return await _faceThumbnails.GetOrCreateAsync(face, cancellationToken).ConfigureAwait(false);
    }

    public ObservableCollection<PersonSummary> People { get; } = [];
    public ObservableCollection<UnassignedFaceViewModel> UnassignedFaces { get; } = [];
    public ObservableCollection<UnassignedFaceViewModel> AssignedFaces { get; } = [];
    public ObservableCollection<CandidateClusterViewModel> CandidateClusters { get; } = [];
    public ObservableCollection<SuggestedPersonGroupViewModel> SuggestedPersonGroups { get; } = [];
    public ObservableCollection<UnassignedFaceViewModel> SelectedClusterFaces { get; } = [];
    public ObservableCollection<UnassignedFaceViewModel> DisplayedFaces { get; } = [];
    public ObservableCollection<VideoFaceItemViewModel> PersonVideoFaces { get; } = [];
    public ObservableCollection<string> FaceModelVersions { get; } = [];
    public ObservableCollection<FaceModelOption> FaceModelOptions { get; } = [];
    public ObservableCollection<FaceTrainingHistoryOption> TrainingHistoryOptions { get; } = [];

    [ObservableProperty] private PersonSummary? _selectedPerson;
    [ObservableProperty] private PersonSummary? _candidateTargetPerson;
    [ObservableProperty] private UnassignedFaceViewModel? _selectedFace;
    [ObservableProperty] private CandidateClusterViewModel? _selectedCandidateCluster;
    [ObservableProperty] private SuggestedPersonGroupViewModel? _selectedSuggestedPersonGroup;
    [ObservableProperty] private UnassignedFaceViewModel? _selectedClusterFace;
    [ObservableProperty] private UnassignedFaceViewModel? _selectedDisplayedFace;
    [ObservableProperty] private string _displayedFacesTitle = "选择左侧分类查看人脸";
    [ObservableProperty] private string _unassignedSummary = "待处理人脸";
    public DisplayedFaceCollectionKind DisplayedCollectionKind { get; private set; }
    [ObservableProperty] private string _newPersonName = "";
    [ObservableProperty] private string _statusText = "人物分析尚未开始";
    [ObservableProperty] private string _trainingResultText = "训练制品：尚未应用";
    [ObservableProperty] private string _facePartitionSummary = "人脸分区：正在校验…";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _showOrganizeProgress;
    [ObservableProperty] private string _organizeProgressText = "尚未运行自动归类";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelAnalysisCommand))]
    private bool _isAnalyzing;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OrganizeButtonText))]
    [NotifyCanExecuteChangedFor(nameof(OrganizeFacesCommand))]
    private bool _isOrganizing;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FaceModelSpaceText))]
    [NotifyPropertyChangedFor(nameof(PeoplePaneTitle))]
    private string? _selectedFaceModelVersion;
    [ObservableProperty] private FaceModelOption? _selectedFaceModelOption;
    [ObservableProperty] private FaceTrainingHistoryOption? _selectedTrainingHistory;

    partial void OnSelectedFaceModelVersionChanged(string? value)
    {
        AppLogger.Info($"People model selection changed: version={value ?? "<null>"}; loaded={_isLoaded}");
        if (!_isLoaded || string.IsNullOrWhiteSpace(value)) return;
        var generation = Interlocked.Increment(ref _modelSwitchGeneration);
        var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _modelSwitchCancellation, cancellation)?.Cancel();
        _personToRestoreAfterModelSwitch = SelectedPerson?.Id ?? 0;
        ResetModelSpaceForSwitch(value);
        _ = SwitchFaceModelAsync(value, generation, cancellation);
    }

    private void ResetModelSpaceForSwitch(string modelVersion)
    {
        AppLogger.Info($"People model switch reset: target={modelVersion}; cancelPreviousLoads=true");
        // Cancel every presentation load from the previous model before
        // clearing its collections. Otherwise an old named-person/candidate
        // task can finish later and repopulate the new model's pane.
        _pageLoadCancellation?.Cancel();
        _personLoadCancellation?.Cancel();
        _clusterLoadCancellation?.Cancel();
        _collectionLoadCancellation?.Cancel();
        // An automatic-organize pass started while the old model was visible
        // must not finish later and repopulate the newly selected model's
        // presentation.  The explicit organize command can be run again once
        // the new space is loaded.
        _organizeCancellation?.Cancel();
        // Do not leave the previous model's faces actionable while its
        // replacement is loading.  Clearing only the presentation collections
        // keeps the database untouched and makes a rapid second switch safe.
        SelectedPerson = null;
        SelectedCandidateCluster = null;
        SelectedSuggestedPersonGroup = null;
        SelectedFace = null;
        SelectedClusterFace = null;
        SelectedDisplayedFace = null;
        DisplayedFaces.Clear();
        AssignedFaces.Clear();
        SelectedClusterFaces.Clear();
        PersonVideoFaces.Clear();
        UnassignedFaces.Clear();
        CandidateClusters.Clear();
        SuggestedPersonGroups.Clear();
        TrainingHistoryOptions.Clear();
        UnassignedSummary = $"正在切换到 {GetFaceModelDisplayName(modelVersion)}…";
        FacePartitionSummary = "正在加载新的模型空间…";
        DisplayedFacesTitle = "正在加载模型空间";
        StatusText = $"正在切换到 {GetFaceModelDisplayName(modelVersion)}，旧分类已暂时隐藏…";
        IsLoading = true;
    }

    partial void OnSelectedFaceModelOptionChanged(FaceModelOption? value)
    {
        AppLogger.Info($"People model option changed: option={value?.ModelVersion ?? "<null>"}; display={value?.DisplayName ?? "<null>"}");
        if (value is not null && !string.Equals(SelectedFaceModelVersion, value.ModelVersion, StringComparison.Ordinal))
            SelectedFaceModelVersion = value.ModelVersion;
        // The option may be restored with the same version value; notify the
        // computed status text explicitly so it cannot remain at “未选择”.
        OnPropertyChanged(nameof(FaceModelSpaceText));
        OnPropertyChanged(nameof(PeoplePaneTitle));
    }

    partial void OnSelectedTrainingHistoryChanged(FaceTrainingHistoryOption? value)
    {
        OnPropertyChanged(nameof(TrainingHistoryText));
        OnPropertyChanged(nameof(FaceModelSpaceText));
        OnPropertyChanged(nameof(PeoplePaneTitle));
    }

    public string TrainingHistoryText => SelectedTrainingHistory is null
        ? "未选择历史模型"
        : $"历史准确率 {SelectedTrainingHistory.ValidationAccuracy:P1} · {SelectedTrainingHistory.ModelVersion}";

    public string FaceModelSpaceText => string.IsNullOrWhiteSpace(SelectedFaceModelVersion)
        ? "模型空间：未选择"
        : $"模型空间：{GetFaceModelDisplayName(SelectedFaceModelVersion)} · 版本 {SelectedFaceModelVersion} · " +
          (SelectedTrainingHistory is null ? "暂无已应用训练准确率" : $"准确率 {SelectedTrainingHistory.ValidationAccuracy:P1}") +
          "（自动归类独立；已命名/不分类共享）";

    public string PeoplePaneTitle => $"人物分类 · {CurrentClassificationModelLabel()}";

    public string OrganizeButtonText => IsOrganizing ? "自动归类进行中…" : "更新自动归类";

    public bool IsModelAvailable => _analysisService is not null;
    public PersonRecord? FindByName(string name) => _peopleService.FindByName(name);
    public PersonQueryMention? FindMention(string query) => _peopleService.FindMention(query);

    public Task RefreshSharedIdentityAsync() => _isLoaded ? RefreshAsync() : Task.CompletedTask;

    public async Task InitializePageAsync()
    {
        if (_isLoaded || IsLoading) return;
        var initializationTimer = Stopwatch.StartNew();
        IsLoading = true;
        StatusText = "正在加载人物分类…";
        var modelVersions = await UiWork.RunDatabaseAsync(() =>
        {
            var versionCounts = _repository.GetFaceModelVersionCounts();
            var versions = versionCounts.Select(value => value.Version).ToArray();
            var sface = versionCounts.Where(value => value.Version.Contains("sface", StringComparison.OrdinalIgnoreCase))
                .Select(value => (Version: value.Version, Count: value.Count))
                .OrderByDescending(value => value.Count).ThenBy(value => value.Version)
                .FirstOrDefault();
            return versions.Where(value => !value.Contains("sface", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, sface.Version, StringComparison.OrdinalIgnoreCase)).ToArray();
        });
        FaceModelVersions.Clear();
        FaceModelOptions.Clear();
        foreach (var version in modelVersions)
        {
            FaceModelVersions.Add(version);
            FaceModelOptions.Add(new FaceModelOption(version, GetFaceModelDisplayName(version)));
        }
        var selectedVersion = FaceModelVersions.FirstOrDefault(value => value.Contains("sface", StringComparison.OrdinalIgnoreCase))
            ?? FaceModelVersions.FirstOrDefault();
        SelectedFaceModelVersion = selectedVersion;
        SelectedFaceModelOption = FaceModelOptions.FirstOrDefault(value => value.ModelVersion == selectedVersion);
        // Publish the model-space selector before loading thousands of face
        // rows/thumbnails. The page can render and accept a model choice
        // immediately; the expensive face refresh continues asynchronously.
        _isLoaded = true;
        IsLoading = false;
        StatusText = "模型空间已加载，正在后台加载人物分类数据…";
        AppLogger.Info($"People model options ready: count={FaceModelOptions.Count}; elapsedMs={initializationTimer.ElapsedMilliseconds}");
        await RefreshAsync();
        if (!string.IsNullOrWhiteSpace(selectedVersion)) await LoadTrainingHistoryAsync(selectedVersion);
        _ = LoadPlatformModelOptionsAsync();
        _ = PublishEvaluationSnapshotAsync();

        var summary = await UiWork.RunDatabaseAsync(_repository.GetSummary);
        if (summary.CompletedImages == 0 || IsAnalyzing) return;
        StatusText = "已显示人物分类，正在后台更新自动归类…";
        var fingerprint = await UiWork.RunDatabaseAsync(GetOrganizeFingerprint);
        if (string.Equals(fingerprint, _lastOrganizeFingerprint, StringComparison.Ordinal))
        {
            StatusText = "人物界面已刷新；自动归类参数和模型没有变化，跳过重复计算";
            return;
        }
        await OrganizeFacesAsync();
        _lastOrganizeFingerprint = await UiWork.RunDatabaseAsync(GetOrganizeFingerprint);
    }

    private static string GetFaceModelDisplayName(string version)
    {
        var normalized = version.Trim().ToLowerInvariant();
        if (normalized.StartsWith("adaptive:", StringComparison.Ordinal))
            return "自适应 YuNet + SFace";
        if (normalized.Contains("sface", StringComparison.Ordinal))
            return "SFace";
        if (normalized is "insightface-arcface" or "insightface")
            return "自适应 InsightFace（YuNet 320/640 + ArcFace）";
        if (normalized == "arcface") return "ArcFace";
        if (normalized == "adaface") return "AdaFace";
        if (normalized == "arcface-paddle") return "ArcFace Paddle";
        if (normalized == "facex-zoo") return "FaceX-Zoo";
        return version.Replace('_', ' ').Replace('-', ' ');
    }

    private string CurrentClassificationModelLabel()
    {
        var version = SelectedFaceModelVersion;
        if (string.IsNullOrWhiteSpace(version)) return "模型未选择";
        var accuracy = SelectedTrainingHistory is { ModelVersion: var selected }
                       && string.Equals(selected, version, StringComparison.OrdinalIgnoreCase)
            ? $" · 准确率 {SelectedTrainingHistory.ValidationAccuracy:P1}" : " · 暂无训练准确率";
        return $"模型 {GetFaceModelDisplayName(version)} ({version}){accuracy}";
    }

    private async Task LoadPlatformModelOptionsAsync()
    {
        try
        {
            var endpoint = Environment.GetEnvironmentVariable("PICER_MODEL_SERVER_URL")?.Trim() ?? "http://127.0.0.1:8090";
            var key = Environment.GetEnvironmentVariable("PICER_MODEL_SERVER_API_KEY")?.Trim() ?? "";
            using var platform = new FaceTrainingSubmissionService(endpoint, key);
            var models = await platform.GetAvailableAlgorithmsAsync().ConfigureAwait(false);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                foreach (var model in models.Where(value => value.Available && !FaceModelOptions.Any(option =>
                             string.Equals(option.ModelVersion, value.ModelVersion, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(GetFaceModelDisplayName(option.ModelVersion), GetFaceModelDisplayName(value.ModelVersion), StringComparison.OrdinalIgnoreCase))))
                {
                    FaceModelOptions.Add(new FaceModelOption(model.ModelVersion, GetFaceModelDisplayName(model.ModelVersion)));
                }
            });
        }
        catch (Exception exception)
        {
            AppLogger.Warn($"读取平台人脸模型列表失败：{exception.Message}");
        }
    }

    private async Task SwitchFaceModelAsync(string modelVersion, long generation, CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        var gateAcquired = false;
        try
        {
            // This workflow updates WPF-bound properties and commands. Keep
            // the continuation on the dispatcher thread; ConfigureAwait(false)
            // here causes OrganizeFacesAsync to raise CanExecuteChanged from a
            // worker thread and leaves the previous model rendered.
            await _modelSwitchGate.WaitAsync(cancellationToken);
            gateAcquired = true;
            if (generation != Volatile.Read(ref _modelSwitchGeneration) ||
                !string.Equals(SelectedFaceModelVersion, modelVersion, StringComparison.OrdinalIgnoreCase)) return;
            StatusText = $"正在检查 {GetFaceModelDisplayName(modelVersion)} 的人脸向量覆盖率…";
            // History is independent from local vector coverage. Do not block
            // switching the face space on the platform archive: a remote
            // archive can be slow/unreachable while the mirrored index is
            // already fully usable. The history task updates the combo box
            // when it completes, while the model-specific faces continue
            // loading immediately.
            var historyTask = LoadTrainingHistoryAsync(modelVersion);
            var (sourceModel, sourceCount, targetCount, targetEmbeddings, missingCount) = await UiWork.RunDatabaseAsync(() =>
            {
                var versionCounts = _repository.GetFaceModelVersionCounts();
                var source = versionCounts.Where(value => !string.Equals(value.Version, modelVersion, StringComparison.OrdinalIgnoreCase))
                    .Select(value => (Version: value.Version, Count: value.Count))
                    // 必须先选择覆盖率最高的人脸框来源；自适应版本只有在覆盖率
                    // 相当时才优先，否则会把完整的 8000+ 张脸误缩成少量自适应记录。
                    .OrderByDescending(value => value.Count)
                    .ThenByDescending(value => value.Version.StartsWith("adaptive:", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
                var targetCount = versionCounts.FirstOrDefault(value =>
                    string.Equals(value.Version, modelVersion, StringComparison.OrdinalIgnoreCase)).Count;
                var targetEmbeddings = _repository.GetFaceEmbeddingCount(modelVersion);
                var missing = Math.Max(0, targetCount - targetEmbeddings);
                return (source.Version, source.Count, targetCount, targetEmbeddings, missing);
            });
            AppLogger.Info($"Face model switch coverage: model={modelVersion}; source={sourceModel}/{sourceCount}; target={targetEmbeddings}/{targetCount}; missing={missingCount}");

            // A model space is a view over its own persisted vectors and
            // workflow records.  It must not depend on another model's
            // detector count merely to become visible.  In particular, a
            // remote mirror can legitimately contain InsightFace vectors
            // while its SFace/YuNet source rows are still being refreshed.
            // The old source-count guard made a valid model switch silently
            // abort and left the previous (usually SFace) faces on screen.

            // Different detectors legitimately produce different face counts
            // (InsightFace does not have to match YuNet/SFace's 10k boxes).
            // Comparing targetCount with the source detector count made every
            // page entry rebuild the same partial target index forever. Once
            // the target model has persisted vectors, keep them and do not
            // rerun an expensive backfill on every navigation.
            // Publish the selected model's people/queue before any optional
            // vector backfill. This keeps the page responsive even when the
            // first switch has to generate missing embeddings.
            await RefreshAsync(loadFaces: false);
            _ = ShowUnassignedFacesCoreAsync(collapseBursts: false);
            // The page is now usable. Keep the progress text visible while a
            // missing-vector backfill completes, but do not disable the face
            // controls for that background maintenance work.
            if (generation == Volatile.Read(ref _modelSwitchGeneration)) IsLoading = false;
            if (missingCount > 0)
            {
                StatusText = $"正在后台生成 {GetFaceModelDisplayName(modelVersion)} 向量：已有 {targetEmbeddings}/{targetCount}，待补齐 {missingCount}…";
                var endpoint = Environment.GetEnvironmentVariable("PICER_MODEL_SERVER_URL")?.Trim() ?? "http://127.0.0.1:8090";
                var key = Environment.GetEnvironmentVariable("PICER_MODEL_SERVER_API_KEY")?.Trim() ?? "";
                using var platform = new FaceTrainingSubmissionService(endpoint, key);
                var service = new RemoteFaceVectorBackfillService(_repository, platform,
                    (source, target, values) => UiWork.RunDatabaseAsync(
                        $"FaceVectorBackfill.Save.{target}",
                        () => _repository.SaveBackfilledEmbeddings(source, target, values)));
                var result = await service.RunAsync(sourceModel, modelVersion,
                    new Progress<FaceVectorBackfillProgress>(value =>
                    {
                        if (!cancellationToken.IsCancellationRequested)
                            StatusText = $"正在生成 {GetFaceModelDisplayName(modelVersion)} 向量：{value.Completed}/{value.Total}，成功 {value.Analyzed}，失败 {value.Failed}";
                    }), cancellationToken);
                if (!result.Completed)
                {
                    StatusText = $"{GetFaceModelDisplayName(modelVersion)} 向量生成未完成：失败 {result.Failed} 条，请修复服务后重试";
                    return;
                }
                StatusText = result.Failed == 0
                    ? $"{GetFaceModelDisplayName(modelVersion)} 向量补齐完成：{result.Analyzed}/{result.Total}，正在加载独立分类…"
                    : $"{GetFaceModelDisplayName(modelVersion)} 向量补齐未完成：成功 {result.Analyzed}，失败 {result.Failed}；已停止切换，请修复后重试";
                if (result.Failed > 0) return;
            }
            // 每个模型空间必须独立显示已经持久化的建议和待命名组，不能沿用其他模型空间的结果。
            // Do not rebuild automatic classification on every ComboBox
            // change.  That operation is expensive, mutates workflow data,
            // and held the switch gate long enough for a second switch to
            // appear as "Insight selected, SFace still shown".  Explicit
            // "更新自动归类" remains the only entry point that rebuilds it.
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _modelSwitchGeneration)) return;
            if (generation != Volatile.Read(ref _modelSwitchGeneration)) return;
            var restorePersonId = Interlocked.Exchange(ref _personToRestoreAfterModelSwitch, 0);
            if (restorePersonId > 0 && generation == Volatile.Read(ref _modelSwitchGeneration))
            {
                var restored = People.FirstOrDefault(person => person.Id == restorePersonId);
                if (restored is not null) SelectedPerson = restored;
            }
            // The archive request is deliberately best-effort. If it is still
            // in flight, keep the newly loaded faces visible and let it fill
            // the artifact list later instead of making the page appear stuck.
            if (historyTask.IsCompleted)
                await historyTask;
            StatusText = $"已切换到 {GetFaceModelDisplayName(modelVersion)}；自动归类结果独立显示，已命名/不分类仍共享";
            AppLogger.Info($"Face model switch completed: model={modelVersion}; displayed={DisplayedFaces.Count}; unassigned={UnassignedFaces.Count}; candidates={CandidateClusters.Count}");
        }
        catch (Exception exception)
        {
            StatusText = $"切换 {GetFaceModelDisplayName(modelVersion)} 失败：{exception.Message}";
            AppLogger.Error($"切换人脸模型空间失败：model={modelVersion}", exception);
        }
        finally
        {
            if (gateAcquired) _modelSwitchGate.Release();
            Interlocked.CompareExchange(ref _modelSwitchCancellation, null, cancellation);
            if (generation == Volatile.Read(ref _modelSwitchGeneration)) IsLoading = false;
            cancellation.Dispose();
        }
    }

    private async Task LoadTrainingHistoryAsync(string modelVersion)
    {
        var requestedGeneration = Volatile.Read(ref _modelSwitchGeneration);
        var aliases = GetModelVersionAliases(modelVersion);
        var history = await UiWork.RunDatabaseAsync(() => aliases
            .SelectMany(version => _repository.GetFaceTrainingHistory(version))
            .GroupBy(value => value.ArtifactId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray());
        // Publish local history before touching the network.  A slow or
        // unavailable ModelServer must never make a valid local artifact
        // dropdown appear empty during a model switch.
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (!string.Equals(SelectedFaceModelVersion, modelVersion, StringComparison.OrdinalIgnoreCase))
                return;
            TrainingHistoryOptions.Clear();
            foreach (var value in history.Where(value => value.ValidationAccuracy >= .90))
                TrainingHistoryOptions.Add(new(value.ArtifactId, value.ModelVersion, value.ValidationAccuracy, value.CreatedUtc, value.State));
            SelectedTrainingHistory = TrainingHistoryOptions.FirstOrDefault(value => value.State == "active") ?? TrainingHistoryOptions.FirstOrDefault();
        });
        IReadOnlyList<RemoteTrainingHistoryData> remote = [];
        try
        {
            var key = Environment.GetEnvironmentVariable("PICER_MODEL_SERVER_API_KEY")?.Trim() ?? "";
            var configuredEndpoint = Environment.GetEnvironmentVariable("PICER_MODEL_SERVER_URL")?.Trim();
            var endpoints = new[] { configuredEndpoint, "http://127.0.0.1:8090" }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var remoteRecords = new List<RemoteTrainingHistoryData>();
            foreach (var endpoint in endpoints)
            {
                // A dead configured endpoint must not consume the cancellation
                // budget of the localhost fallback.  The old shared token made
                // the fallback immediately return [] after one slow endpoint.
                using var endpointTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                try
                {
                    using var platform = new FaceTrainingSubmissionService(endpoint!, key);
                    remoteRecords = (await Task.WhenAll(aliases.Select(alias => platform.GetTrainingHistoryAsync(alias, endpointTimeout.Token)))
                            .ConfigureAwait(false)).SelectMany(value => value).ToList();
                }
                catch (Exception endpointException)
                {
                    AppLogger.Warn($"读取训练历史端点失败：endpoint={endpoint}; error={endpointException.Message}");
                    remoteRecords.Clear();
                }
                if (remoteRecords.Count > 0) break;
            }
            AppLogger.Info($"Face training history endpoint query: model={modelVersion}; aliases={string.Join(',', aliases)}; records={remoteRecords.Count}");
            remote = remoteRecords.GroupBy(value => value.ArtifactId ?? $"{value.ModelVersion}:{value.CreatedUtc:o}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToArray();
        }
        catch (Exception exception) { AppLogger.Warn($"加载平台训练历史失败：{exception.Message}"); }
        var remoteOptions = new List<FaceTrainingHistoryOption>();
        var remoteCandidates = remote.Where(value => (value.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)
                     || value.Status.Equals("applied", StringComparison.OrdinalIgnoreCase))
                     && value.ValidationAccuracy >= .90 && !string.IsNullOrWhiteSpace(value.ArtifactId)).ToArray();
        // Artifact verification is best effort.  History must still populate
        // the ComboBox when an artifact endpoint is slow or temporarily
        // unavailable; otherwise the model switch looks like it has no
        // versions at all.  Applying an unavailable entry remains blocked by
        // CanApply=false.
        var artifacts = await Task.WhenAll(remoteCandidates.Select(async value =>
        {
            var artifact = await TryGetRemoteArtifactAsync(value.ArtifactId!).ConfigureAwait(false);
            return (value, Available: artifact is not null);
        })).ConfigureAwait(false);
        foreach (var item in artifacts)
        {
            var value = item.value;
            remoteOptions.Add(new(value.ArtifactId!, value.ModelVersion, value.ValidationAccuracy!.Value,
                value.CreatedUtc, value.Status, CanApply: item.Available));
        }
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (!string.Equals(SelectedFaceModelVersion, modelVersion, StringComparison.OrdinalIgnoreCase))
                return;
            TrainingHistoryOptions.Clear();
            foreach (var value in history.Where(value => value.ValidationAccuracy >= .90))
                TrainingHistoryOptions.Add(new(value.ArtifactId, value.ModelVersion, value.ValidationAccuracy, value.CreatedUtc, value.State));
            // Small synthetic multi-model baseline probes are not user training
            // versions and must not appear as selectable history.
            foreach (var value in remoteOptions
                         .Where(value => !TrainingHistoryOptions.Any(item => string.Equals(item.ArtifactId, value.ArtifactId, StringComparison.OrdinalIgnoreCase))))
            {
                TrainingHistoryOptions.Add(value);
            }
            SelectedTrainingHistory = TrainingHistoryOptions.FirstOrDefault(value => value.State == "active") ?? TrainingHistoryOptions.FirstOrDefault();
        });
        AppLogger.Info($"Face training history loaded: model={modelVersion}; local={history.Length}; remote={remoteOptions.Count}; currentGeneration={Volatile.Read(ref _modelSwitchGeneration)}; requestedGeneration={requestedGeneration}");
    }

    private static IReadOnlyList<string> GetModelVersionAliases(string modelVersion)
    {
        if (modelVersion.Contains("insightface", StringComparison.OrdinalIgnoreCase))
            return new[] { modelVersion, "insightface", "insightface-arcface" }
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new[] { modelVersion };
    }

    private static bool IsSameModelFamily(string left, string right)
        => GetModelVersionAliases(left).Contains(right, StringComparer.OrdinalIgnoreCase) ||
           GetModelVersionAliases(right).Contains(left, StringComparer.OrdinalIgnoreCase);

    [RelayCommand]
    private async Task ApplySelectedTrainingAsync()
    {
        if (IsAnalyzing || SelectedTrainingHistory is null) return;
        var modelVersion = SelectedFaceModelVersion ?? "sface";
        if (!IsSameModelFamily(modelVersion, SelectedTrainingHistory.ModelVersion))
        {
            StatusText = "历史模型与当前模型空间不一致，已阻止应用";
            return;
        }
        IsAnalyzing = true;
        try
        {
            var artifact = await UiWork.RunDatabaseAsync(() => _repository.GetFaceTrainingArtifact(SelectedTrainingHistory.ArtifactId, modelVersion));
            if (artifact is null && SelectedTrainingHistory.CanApply)
                artifact = await TryGetRemoteArtifactAsync(SelectedTrainingHistory.ArtifactId);
            if (artifact is null && !SelectedTrainingHistory.CanApply)
            {
                StatusText = "该平台历史记录仅保留了评估指标，训练制品已不在当前平台，无法直接应用；请按该配方重新提交训练";
                return;
            }
            if (artifact is null) { StatusText = "历史训练制品不存在，无法应用"; return; }
            await UiWork.RunDatabaseAsync(() => ApplyTrainedPrototypes(artifact));
            TrainingResultText = $"已应用历史模型：{artifact.ModelVersion} · 准确率 {artifact.Metrics.ValidationAccuracy:P1} · 制品 {artifact.ArtifactId}";
            StatusText = $"已应用 {GetFaceModelDisplayName(modelVersion)} 的历史训练版本，分类结果将按该模型空间刷新";
            await RefreshPageAsync();
        }
        finally { IsAnalyzing = false; }
    }

    private async Task<TrainingArtifactData?> TryGetRemoteArtifactAsync(string artifactId, CancellationToken cancellationToken = default)
    {
        var configuredEndpoint = Environment.GetEnvironmentVariable("PICER_MODEL_SERVER_URL")?.Trim();
        var endpoints = new[] { configuredEndpoint, "http://127.0.0.1:8090" }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var key = Environment.GetEnvironmentVariable("PICER_MODEL_SERVER_API_KEY")?.Trim() ?? "";
        foreach (var endpoint in endpoints)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                using var platform = new FaceTrainingSubmissionService(endpoint!, key);
                var artifact = await platform.GetArtifactAsync(artifactId, timeout.Token).ConfigureAwait(false);
                if (artifact is not null) return artifact;
            }
            catch (Exception exception)
            {
                AppLogger.Warn($"读取训练制品失败：endpoint={endpoint}; artifact={artifactId}; error={exception.Message}");
            }
        }
        return null;
    }

    public Task PublishEvaluationSnapshotForPlatformAsync() => PublishEvaluationSnapshotAsync();

    private async Task PublishEvaluationSnapshotAsync()
    {
        if (Interlocked.Exchange(ref _baselinePublishStarted, 1) != 0) return;
        AppLogger.Info("统一基线评估集发布开始");
        try
        {
            var endpoint = Environment.GetEnvironmentVariable("PICER_MODEL_SERVER_URL")?.Trim() ?? "http://127.0.0.1:8090";
            var key = Environment.GetEnvironmentVariable("PICER_MODEL_SERVER_API_KEY")?.Trim() ?? "";
            using var platform = new FaceTrainingSubmissionService(endpoint, key);
            var snapshot = await UiWork.RunDatabaseAsync(() => new FaceEvaluationSnapshotService(_repository)
                .CreateSnapshot(dataVersion: "face-baseline-v1", algorithmId: "multi-model-baseline"));
            var publishedSnapshot = await platform.GetPublishedEvaluationSnapshotIdAsync();
            if (string.Equals(publishedSnapshot, snapshot.SnapshotId, StringComparison.Ordinal))
            {
                AppLogger.Info($"统一基线评估集指纹未变化，跳过重复上传：snapshot={snapshot.SnapshotId}");
                return;
            }
            var members = await UiWork.RunDatabaseAsync(() => new FaceEvaluationSnapshotService(_repository).GetMembers(snapshot.SnapshotId));
            var sources = await UiWork.RunDatabaseAsync(() => _repository.GetFaceSources(snapshot.EmbeddingModelVersion));
            var byFace = sources.ToDictionary(source => source.FaceId);
            var samples = new List<(long FaceId, long PersonId, string Split, string Group, string FaceImageBase64)>();
            var membersToEncode = members.Where(member => member.FaceId is not null)
                .Where(member => byFace.ContainsKey(member.FaceId!.Value)).ToArray();
            var encoded = new System.Collections.Concurrent.ConcurrentBag<(long FaceId, long PersonId, string Split, string Group, string Crop)>();
            var completed = 0;
            await Parallel.ForEachAsync(membersToEncode, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) },
                async (member, cancellationToken) =>
                {
                    var source = byFace[member.FaceId!.Value];
                    var crop = await Task.Run(() => RemoteFaceVectorBackfillService.EncodeCrop(source, cancellationToken), cancellationToken);
                    if (!string.IsNullOrWhiteSpace(crop))
                        encoded.Add((member.FaceId.Value, member.PersonId, member.Partition, member.LeakageGroup, crop));
                    var done = Interlocked.Increment(ref completed);
                    if (done % 250 == 0 || done == membersToEncode.Length)
                        AppLogger.Info($"统一基线评估集裁剪进度：{done}/{membersToEncode.Length}");
                });
            samples.AddRange(encoded.Select(item => (item.FaceId, item.PersonId, item.Split, item.Group, item.Crop)));
            AppLogger.Info($"统一基线评估集已生成：snapshot={snapshot.SnapshotId}; samples={samples.Count}");
            var result = await platform.PublishEvaluationDatasetAsync(snapshot.SnapshotId, samples);
            if (!result.Success) AppLogger.Warn($"统一基线评估集上传失败：{result.Error}");
            else AppLogger.Info($"已发布统一基线评估集：snapshot={snapshot.SnapshotId}; samples={samples.Count}");
        }
        catch (Exception exception) { AppLogger.Warn($"准备统一基线评估集失败：{exception.Message}"); }
    }

    private string GetOrganizeFingerprint()
    {
        using var command = _database.Command("""
            SELECT (SELECT COUNT(*) FROM face_person_assignments WHERE state IN ('confirmed','suggested')),
                   (SELECT COUNT(*) FROM people),
                   (SELECT COUNT(*) FROM face_person_prototypes),
                   (SELECT COALESCE(MAX(updated_utc), '') FROM face_person_prototypes),
                   (SELECT COALESCE(MAX(id), 0) FROM face_action_log),
                   (SELECT COUNT(*) FROM face_person_assignments WHERE state='suggested')
            """);
        using var reader = command.ExecuteReader();
        return reader.Read() ? string.Join('|', Enumerable.Range(0, 6).Select(reader.GetValue)) : "empty";
    }

    private async Task RefreshAsync(bool loadFaces = true)
    {
        _pageLoadCancellation?.Cancel();
        _personLoadCancellation?.Cancel();
        _clusterLoadCancellation?.Cancel();
        _collectionLoadCancellation?.Cancel();
        var cancellation = _pageLoadCancellation = new CancellationTokenSource();
        var selectedPersonId = SelectedPerson?.Id;
        var selectedClusterId = SelectedCandidateCluster?.Record.Id;
        var selectedSuggestedPersonId = SelectedSuggestedPersonGroup?.Record.PersonId;
        var restoreIgnored = DisplayedFacesTitle.StartsWith("不分类", StringComparison.Ordinal);
        var restoreUnassigned = DisplayedFacesTitle.StartsWith("待分类", StringComparison.Ordinal);
        var modelVersion = SelectedFaceModelVersion;
        var snapshot = await UiWork.RunDatabaseAsync(() => (
            People: _peopleService.GetAll().ToArray(),
            Faces: _repository.GetUnassignedFaces(modelVersion: modelVersion).ToArray(),
            Clusters: _repository.GetCandidateClusters(modelVersion).ToArray(),
            SuggestedGroups: _repository.GetSuggestedPersonGroups(modelVersion).ToArray(),
            WorkQueue: _repository.GetFaceWorkQueueCounts(modelVersion),
            Partition: string.IsNullOrWhiteSpace(modelVersion)
                ? new FacePartitionCounts(0, 0, 0, 0, 0, 0)
                : _repository.GetFacePartitionCounts(modelVersion),
            // “不分类”是用户明确做出的跨模型人工确认，始终共享。
            IgnoredCount: _repository.GetIgnoredFaceCount(),
            Summary: _repository.GetSummary()), cancellation.Token);
        cancellation.Token.ThrowIfCancellationRequested();
        AppLogger.Info($"People refresh snapshot: model={modelVersion}; faces={snapshot.Faces.Length}; candidates={snapshot.Clusters.Length}; suggestedGroups={snapshot.SuggestedGroups.Length}; partition={snapshot.Partition.Total}/{snapshot.Partition.Ignored}/{snapshot.Partition.Named}/{snapshot.Partition.Suggested}/{snapshot.Partition.Candidate}/{snapshot.Partition.Unassigned}");

        SelectedPerson = null;
        SelectedCandidateCluster = null;
        SelectedFace = null;
        SelectedClusterFace = null;
        SelectedDisplayedFace = null;
        DisplayedFaces.Clear();
        AssignedFaces.Clear();
        PersonVideoFaces.Clear();
        SelectedClusterFaces.Clear();
        People.Clear();
        foreach (var person in snapshot.People) People.Add(person);
        UnassignedFaces.Clear();
        if (loadFaces)
        {
            await UiWork.AddInBatchesAsync(snapshot.Faces, face =>
                {
                    var item = CreateFaceItem(face);
                    UnassignedFaces.Add(item);
                    _ = LoadFaceThumbnailAsync(item);
                }, cancellation.Token);
        }
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
        SuggestedPersonGroups.Clear();
        foreach (var group in snapshot.SuggestedGroups) SuggestedPersonGroups.Add(new SuggestedPersonGroupViewModel(group));
        UnassignedSummary = $"待处理人脸 · {snapshot.WorkQueue.Total}（待确认 {snapshot.WorkQueue.Suggested} / 待命名 {snapshot.WorkQueue.Candidate} / 待分类 {snapshot.WorkQueue.Unassigned}）";
        FacePartitionSummary = $"人脸分区 · 总 {snapshot.Partition.Total} = 不分类 {snapshot.Partition.Ignored} + 已命名 {snapshot.Partition.Named} + 待确认 {snapshot.Partition.Suggested} + 待命名 {snapshot.Partition.Candidate} + 待分类 {snapshot.Partition.Unassigned}";
        if (!snapshot.Partition.IsConserved)
            AppLogger.Error($"人脸分区数量不守恒：model={modelVersion}; total={snapshot.Partition.Total}; ignored={snapshot.Partition.Ignored}; named={snapshot.Partition.Named}; suggested={snapshot.Partition.Suggested}; candidate={snapshot.Partition.Candidate}; unassigned={snapshot.Partition.Unassigned}");
        var summary = snapshot.Summary;
        StatusText = _unavailableReason ??
            $"已分析 {summary.CompletedImages}/{summary.TotalImages} 张照片，发现 {summary.FaceCount} 张人脸，失败 {summary.FailedImages} 张";
        ProgressValue = summary.TotalImages == 0 ? 0 : summary.CompletedImages * 100d / summary.TotalImages;
        var restoredPerson = selectedPersonId is long personId ? People.FirstOrDefault(value => value.Id == personId) : null;
        var restoredCluster = selectedClusterId is long clusterId ? CandidateClusters.FirstOrDefault(value => value.Record.Id == clusterId) : null;
        var restoredSuggestedGroup = selectedSuggestedPersonId is long suggestedPersonId
            ? SuggestedPersonGroups.FirstOrDefault(value => value.Record.PersonId == suggestedPersonId) : null;
        if (restoredPerson is not null) SelectedPerson = restoredPerson;
        else if (restoredSuggestedGroup is not null) SelectedSuggestedPersonGroup = restoredSuggestedGroup;
        else if (restoredCluster is not null) SelectedCandidateCluster = restoredCluster;
        else if (restoreIgnored) await ShowIgnoredFacesAsync();
        else if (loadFaces && (restoreUnassigned || DisplayedFaces.Count == 0)) await ShowUnassignedFacesAsync();
    }

    [RelayCommand]
    private async Task RefreshPageAsync()
    {
        StatusText = "正在同步人物界面…";
        await RefreshAsync();
        if (!string.IsNullOrWhiteSpace(SelectedFaceModelVersion))
            await LoadTrainingHistoryAsync(SelectedFaceModelVersion);
        var ignored = await UiWork.RunDatabaseAsync(() => _repository.GetIgnoredFaceCount());
        OnPropertyChanged(nameof(FaceModelSpaceText));
        StatusText = $"UI 已同步：{People.Count} 位人物，{CandidateClusters.Count} 个候选组，" +
            $"{UnassignedFaces.Count} 张待分类，{ignored} 张不分类";
    }

    [RelayCommand(CanExecute = nameof(CanAnalyzeAll))]
    private async Task AnalyzeAllAsync() => await ProcessNewPhotosAsync();

    public async Task ProcessNewPhotosAsync()
    {
        using var operation = AppLogger.BeginOperation("people.analyze-and-organize");
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
            operation.Cancel("user-canceled");
            StatusText = "人物分析已取消，已完成结果仍然有效";
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
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

    [RelayCommand]
    private async Task BackfillInsightFaceAsync()
    {
        if (IsAnalyzing) return;
        var endpoint = Environment.GetEnvironmentVariable("PICER_MODEL_SERVER_URL")?.Trim() ?? "http://127.0.0.1:8090";
        var key = Environment.GetEnvironmentVariable("PICER_MODEL_SERVER_API_KEY")?.Trim() ?? "";
        IsAnalyzing = true;
        try
        {
            using var platform = new FaceTrainingSubmissionService(endpoint, key);
            var available = await platform.GetAvailableAlgorithmsAsync();
            if (!available.Any(model => model.Available && model.ModelVersion == "insightface-arcface"))
            {
                StatusText = "InsightFace 尚未就绪，未启动补齐";
                return;
            }
            var service = new RemoteFaceVectorBackfillService(_repository, platform,
                (source, target, values) => UiWork.RunDatabaseAsync(
                    $"FaceVectorBackfill.Save.{target}",
                    () => _repository.SaveBackfilledEmbeddings(source, target, values)));
            var sourceModel = _repository.GetFaceModelVersions()
                .Select(model => (Version: model, Count: _repository.GetFaceSources(model).Count))
                .OrderByDescending(value => value.Count)
                .ThenByDescending(value => value.Version.StartsWith("adaptive:", StringComparison.OrdinalIgnoreCase))
                .Select(value => value.Version)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(sourceModel))
            {
                StatusText = "索引中没有可用于裁剪的人脸向量";
                return;
            }
            var result = await service.RunAsync(sourceModel, "insightface-arcface",
                new Progress<FaceVectorBackfillProgress>(value => StatusText = $"正在补齐 InsightFace 向量：{value.Completed}/{value.Total}，成功 {value.Analyzed}，失败 {value.Failed}"));
            StatusText = result.Completed
                ? $"InsightFace 向量补齐完成：{result.Analyzed}/{result.Total}"
                : $"InsightFace 向量补齐未完成：成功 {result.Analyzed}，失败 {result.Failed}，可重试";
        }
        catch (Exception exception)
        {
            StatusText = $"InsightFace 向量补齐失败：{exception.Message}";
            AppLogger.Error("InsightFace 向量补齐失败", exception);
        }
        finally { IsAnalyzing = false; }
    }

    [RelayCommand(CanExecute = nameof(CanCancelAnalysis))]
    private void CancelAnalysis() => _analysisCancellation?.Cancel();

    private bool CanCancelAnalysis() => IsAnalyzing && _analysisCancellation is not null;

    [RelayCommand]
    private async Task StartTrainingAsync()
    {
        var eligibility = await UiWork.RunDatabaseAsync(() => FaceTrainingSnapshotService.CheckEligibility(_database));
        if (eligibility.PositiveFaceCount < 2) { StatusText = "至少需要确认 2 张人脸才能开始训练"; return; }
        IsAnalyzing = true;
        try
        {
            var trainingModelVersion = SelectedFaceModelVersion ?? "sface";
            var snapshot = await UiWork.RunDatabaseAsync(() => FaceTrainingSnapshotService.BuildSnapshot(_database));
            var samples = await UiWork.RunDatabaseAsync(() => ExtractEmbeddings(snapshot.SnapshotId, trainingModelVersion));
            var negativeSamples = await UiWork.RunDatabaseAsync(() => ExtractNegativeEmbeddings(snapshot.SnapshotId, trainingModelVersion));
            if (samples.Count == 0)
            {
                StatusText = $"{GetFaceModelDisplayName(trainingModelVersion)} 尚无可用于训练的完整向量，请先完成该模型的向量补齐";
                return;
            }
            var actions = await UiWork.RunDatabaseAsync(() => FaceActionLogService.GetActions(_database, limit: 100_000)
                .Select(value => new TrainingActionEvidence(value.FaceId, value.PersonId, value.SourcePersonId,
                    value.Action, value.CreatedUtc, value.CoClusteredFaceIds, value.CoClusteredPersonIds,
                    value.ClusterConfidence, value.ModelVersion, value.ErrorType)).ToArray());
            var actionSummary = string.Join("；", actions.GroupBy(value => value.Action, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count()).Select(group => $"{group.Key}={group.Count()}"));
            var endpoint = Environment.GetEnvironmentVariable("PICER_MODEL_SERVER_URL")?.Trim() ?? "http://127.0.0.1:8090";
            string? key = Environment.GetEnvironmentVariable("PICER_MODEL_SERVER_API_KEY")?.Trim();
            using var submission = new FaceTrainingSubmissionService(endpoint, key ?? "");
            var availableModels = await submission.GetAvailableAlgorithmsAsync();
            var readyModels = availableModels.Where(model => model.Available).ToArray();
            StatusText = readyModels.Length == 0
                ? "训练平台没有可用人脸模型，任务不会阻塞在提交阶段"
                : $"训练平台已就绪 {readyModels.Length} 个模型：{string.Join("、", readyModels.Select(model => model.ModelVersion))}；本次仅训练当前模型空间 {GetFaceModelDisplayName(trainingModelVersion)}，不会自动训练其他模型，向量版本不能混用";
            var agent = new FaceTrainingAgent(submission);
            var result = await agent.RunAsync(snapshot, samples,
                (attempt, artifact, token) => AnalyzeTrainingFailureAsync(attempt, artifact, .90, actionSummary, token),
                new Progress<string>(text => StatusText = text),
                actions: actions, initialHints: actionSummary, modelVersion: trainingModelVersion, negativeSamples: negativeSamples);
            if (!result.Passed || result.Artifact is null)
            {
                StatusText = $"训练失败：{result.FailureReason}；制品未应用";
                TrainingResultText = result.Artifact is null ? "训练制品：未生成可应用版本" : $"未应用制品：{result.Artifact.ArtifactId}；准确率 {result.Artifact.Metrics.ValidationAccuracy:P1}";
                return;
            }
            var artifact = result.Artifact;
            var historicalBest = await UiWork.RunDatabaseAsync(() =>
                _repository.GetBestFaceTrainingValidationAccuracy(trainingModelVersion));
            if (artifact.Metrics.ValidationAccuracy < 0.90 ||
                (historicalBest is double best && artifact.Metrics.ValidationAccuracy <= best + 1e-9))
            {
                var bestText = historicalBest is double value ? $"历史最佳 {value:P1}" : "暂无历史最佳";
                StatusText = $"训练未采用：准确率 {artifact.Metrics.ValidationAccuracy:P1}，{bestText}；必须达到 90% 且严格超过历史最佳";
                TrainingResultText = $"未采用制品：{artifact.ArtifactId}；准确率 {artifact.Metrics.ValidationAccuracy:P1}；当前模型保持历史最佳";
                AppLogger.Warn($"训练制品未采用：model={trainingModelVersion}; artifact={artifact.ArtifactId}; accuracy={artifact.Metrics.ValidationAccuracy:F4}; best={historicalBest?.ToString("F4") ?? "none"}");
                return;
            }
            await UiWork.RunDatabaseAsync(() => ApplyTrainedPrototypes(artifact));
            await submission.MarkArtifactAppliedAsync(artifact.ArtifactId);
            // Adoption updates the repository asynchronously; refresh the
            // model-scoped history and current people page immediately so
            // the active artifact/version is visible without page navigation.
            await LoadTrainingHistoryAsync(trainingModelVersion);
            await RefreshPageAsync();
            StatusText = $"训练通过并已应用（第 {result.Attempts} 次）：准确率 {artifact.Metrics.ValidationAccuracy:P1}";
            TrainingResultText = $"已应用模型版本：{artifact.ModelVersion}；制品：{artifact.ArtifactId}；评测：准确率 {artifact.Metrics.ValidationAccuracy:P1} / 误识率 {artifact.Metrics.FalseAcceptRate:P2} / 拒识率 {artifact.Metrics.FalseRejectRate:P2}；本轮达到90%的制品 {result.PassedArtifacts?.Count ?? 1} 个" +
                (string.IsNullOrWhiteSpace(result.ExperienceSummary) ? "" : $"；经验总结：{result.ExperienceSummary}");
        }
        catch (Exception exception)
        {
            StatusText = $"训练失败：{exception.Message}";
            AppLogger.Error("训练 Agent 执行失败", exception);
        }
        finally { IsAnalyzing = false; }
    }

    [RelayCommand]
    private async Task RollbackTrainingAsync()
    {
        if (IsAnalyzing) return;
        var modelVersion = SelectedFaceModelVersion ?? "sface";
        IsAnalyzing = true;
        try
        {
            var artifact = await UiWork.RunDatabaseAsync(() =>
                _repository.GetPreviousAgentTrainingArtifact(modelVersion));
            if (artifact is null)
            {
                StatusText = $"{GetFaceModelDisplayName(modelVersion)} 没有可回退的历史训练版本";
                return;
            }
            await UiWork.RunDatabaseAsync(() => ApplyTrainedPrototypes(artifact));
            TrainingResultText = $"已回退到历史制品：{artifact.ArtifactId}；准确率 {artifact.Metrics.ValidationAccuracy:P1}";
            StatusText = $"已回退 {GetFaceModelDisplayName(modelVersion)} 的上一版训练结果，当前模型未改变人工标注";
            await RefreshPageAsync();
        }
        catch (Exception exception)
        {
            StatusText = $"回退训练结果失败：{exception.Message}";
            AppLogger.Error("回退人物训练制品失败", exception);
        }
        finally { IsAnalyzing = false; }
    }

    private async Task RunBaselineEvaluationAsync()
    {
        if (IsAnalyzing) return;
        IsAnalyzing = true;
        try
        {
            var endpoint = Environment.GetEnvironmentVariable("PICER_MODEL_SERVER_URL")?.Trim() ?? "http://127.0.0.1:8090";
            var key = Environment.GetEnvironmentVariable("PICER_MODEL_SERVER_API_KEY")?.Trim() ?? "";
            using var platform = new FaceTrainingSubmissionService(endpoint, key);
            var available = (await platform.GetAvailableAlgorithmsAsync()).Where(model => model.Available).ToArray();
            if (available.Length == 0)
            {
                StatusText = "基线评估未启动：平台没有可用人脸模型";
                return;
            }
            var sourceModel = await UiWork.RunDatabaseAsync(() => _repository.GetFaceModelVersions()
                .Select(model => (Version: model, Count: _repository.GetFaceSources(model).Count))
                .OrderByDescending(value => value.Count)
                .ThenByDescending(value => value.Version.StartsWith("adaptive:", StringComparison.OrdinalIgnoreCase))
                .Select(value => value.Version)
                .FirstOrDefault());
            if (string.IsNullOrWhiteSpace(sourceModel))
            {
                StatusText = "基线评估未启动：索引中没有可用于裁剪人脸的模型向量";
                return;
            }

            StatusText = $"正在准备真实评估集：以 {sourceModel} 为人脸框来源…";
            foreach (var model in available.Where(model => !string.Equals(model.ModelVersion, sourceModel, StringComparison.OrdinalIgnoreCase)))
            {
                var target = model.ModelVersion;
                var coverage = await UiWork.RunDatabaseAsync(() => (Source: _repository.GetFaceEmbeddingCount(sourceModel),
                    Target: _repository.GetFaceEmbeddingCount(target)));
                if (coverage.Target < coverage.Source)
                {
                    StatusText = $"正在为基线补齐 {target} 向量…";
                    var backfill = new RemoteFaceVectorBackfillService(_repository, platform,
                        (source, targetModel, values) => UiWork.RunDatabaseAsync(
                            $"FaceVectorBackfill.Save.{targetModel}",
                            () => _repository.SaveBackfilledEmbeddings(source, targetModel, values)));
                    var result = await backfill.RunAsync(sourceModel, target,
                        new Progress<FaceVectorBackfillProgress>(value => StatusText =
                            $"正在为基线补齐 {target} 向量：{value.Completed}/{value.Total}，成功 {value.Analyzed}，失败 {value.Failed}"));
                    if (!result.Completed)
                    {
                        StatusText = $"基线评估已阻止：{target} 向量补齐失败 {result.Failed} 条，请先修复后重试";
                        return;
                    }
                }
            }

            var snapshot = await UiWork.RunDatabaseAsync(() => new FaceEvaluationSnapshotService(_repository)
                .CreateSnapshot(dataVersion: "face-baseline-v1", algorithmId: "multi-model-baseline"));
            var members = await UiWork.RunDatabaseAsync(() => new FaceEvaluationSnapshotService(_repository).GetMembers(snapshot.SnapshotId));
            var expected = members.Count(member => member.FaceId is not null);
            var datasets = new Dictionary<string, IReadOnlyList<(long FaceId, long PersonId, float[] Embedding, string Split, string Group)>>();
            foreach (var model in available)
            {
                var samples = await UiWork.RunDatabaseAsync(() => _repository.GetSnapshotModelSamples(snapshot.SnapshotId, model.ModelVersion));
                if (samples.Count != expected)
                {
                    StatusText = $"基线评估已阻止：{model.ModelVersion} 向量覆盖 {samples.Count}/{expected}，不能用不完整数据冒充基线";
                    return;
                }
                datasets[model.ModelVersion] = samples.Select(sample =>
                    (sample.FaceId, sample.PersonId, sample.Embedding, sample.Split, sample.LeakageGroup)).ToArray();
            }

            StatusText = $"正在提交所有模型基线：{datasets.Count} 个模型、快照 {snapshot.SnapshotId}…";
            var submission = await platform.SubmitBaselineAsync(snapshot, datasets);
            if (submission.Jobs.Count == 0)
            {
                StatusText = $"基线提交失败：{submission.Error ?? "平台未返回任务"}";
                return;
            }
            TrainingResultText = $"基线评估已提交：{submission.Jobs.Count} 个模型；评估编号 {submission.EvaluationId}";
            StatusText = $"基线任务已异步启动（{submission.Jobs.Count} 个模型）。完成后在平台排行榜查看：{endpoint.TrimEnd('/')}/picer/";
            AppLogger.Info($"已提交真实多模型基线：evaluation={submission.EvaluationId}; snapshot={snapshot.SnapshotId}; models={string.Join(',', datasets.Keys)}");
        }
        catch (Exception exception)
        {
            StatusText = $"基线评估失败：{exception.Message}";
            AppLogger.Error("提交多模型基线评估失败", exception);
        }
        finally { IsAnalyzing = false; }
    }

    private async Task<string?> AnalyzeTrainingFailureAsync(int attempt, TrainingArtifactData artifact, double target, string actionSummary, CancellationToken cancellationToken)
    {
        try
        {
            var key = Environment.GetEnvironmentVariable("PICER_MODEL_SERVER_API_KEY")?.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                var keyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".picer", "model-server-key");
                if (File.Exists(keyPath)) key = File.ReadAllText(keyPath).Trim();
            }
            if (string.IsNullOrWhiteSpace(key)) return "未配置智谱 API Key；增加困难负样本并调整阈值";
            var platform = Environment.GetEnvironmentVariable("PICER_MODEL_SERVER_URL")?.TrimEnd('/') ?? "http://127.0.0.1:8090";
            if (Uri.TryCreate(platform, UriKind.Absolute, out var platformUri) && platformUri.IsLoopback && platformUri.Port == 8090 && !platformUri.AbsolutePath.Contains("/picer", StringComparison.OrdinalIgnoreCase))
                platform += "/picer";
            using var chat = new OpenAiCompatibleChatClient(new Uri($"{platform}/api/ai/chat/completions"), key, "glm-4.6v-flash");
            return await chat.CompleteAsync("你是人脸训练 Agent。基于历史试验记忆提出下一次可执行调整；只输出简短建议，不输出个人信息。",
                $"第{attempt}次未通过。目标准确率={target:P0}，实际={artifact.Metrics.ValidationAccuracy:P2}，误识率={artifact.Metrics.FalseAcceptRate:P2}，拒识率={artifact.Metrics.FalseRejectRate:P2}，样本={artifact.Metrics.TotalSamples}，人物={artifact.Metrics.TotalPersons}。用户操作摘要：{actionSummary}。请结合拒绝、移动和确认日志给出下一次正负样本和阈值策略建议。", 300, cancellationToken);
        }
        catch (Exception exception) { AppLogger.Warn($"智谱训练分析失败：{exception.Message}"); return "智谱分析不可用；增加困难负样本并调整阈值"; }
    }

    private async Task StartTrainingLegacyAsync()
    {
        var eligibility = await UiWork.RunDatabaseAsync(() => FaceTrainingSnapshotService.CheckEligibility(_database));
        if (eligibility.PositiveFaceCount < 2)
        {
            StatusText = "至少需要确认 2 张人脸才能开始训练";
            return;
        }

        IsAnalyzing = true;
        StatusText = $"正在准备训练数据（{eligibility.PersonCount} 位人物 / {eligibility.PositiveFaceCount} 张已确认人脸）…";
        try
        {
            var trainingModelVersion = SelectedFaceModelVersion ?? "sface";
            var snapshot = await UiWork.RunDatabaseAsync(() => FaceTrainingSnapshotService.BuildSnapshot(_database));
            var samples = await UiWork.RunDatabaseAsync(() => ExtractEmbeddings(snapshot.SnapshotId, trainingModelVersion));
            var negativeSamples = await UiWork.RunDatabaseAsync(() => ExtractNegativeEmbeddings(snapshot.SnapshotId, trainingModelVersion));

            var baseUrl = Environment.GetEnvironmentVariable("PICER_MODEL_SERVER_URL")
                ?.Trim() ?? "http://127.0.0.1:8090";
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var modelServerUri)
                || (modelServerUri.Scheme != Uri.UriSchemeHttp && modelServerUri.Scheme != Uri.UriSchemeHttps))
            {
                StatusText = "训练服务地址无效，请检查 PICER_MODEL_SERVER_URL";
                return;
            }
            string? apiKey = null;
            try
            {
                var keyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".picer", "model-server-key");
                if (File.Exists(keyPath)) apiKey = File.ReadAllText(keyPath).Trim();
            }
            catch { }
            apiKey ??= Environment.GetEnvironmentVariable("PICER_MODEL_SERVER_API_KEY")?.Trim();

            using var submission = new FaceTrainingSubmissionService(modelServerUri.ToString(), apiKey ?? "");
            StatusText = $"正在提交训练任务（{samples.Count} 个样本）…";
            var result = await submission.SubmitAsync(snapshot, samples, negativeSamples: negativeSamples);
            if (result.Status == "failed")
            {
                StatusText = $"训练提交失败：{result.Error}";
                AppLogger.Error($"人脸训练提交失败：status={result.Status}; error={result.Error ?? "unknown"}");
                return;
            }

            StatusText = $"训练已启动（任务 {result.JobId.Substring(0, 12)}…），正在等待完成…";
            var pollCount = 0;
            while (pollCount++ < 120)
            {
                await Task.Delay(1000);
                var status = await submission.PollAsync(result.JobId);
                if (status.Status == "completed" && status.ArtifactId is not null)
                {
                    var artifact = await submission.GetArtifactAsync(status.ArtifactId);
                    if (artifact is not null)
                    {
                        const double minimumTrainingAccuracy = 0.90;
                        if (artifact.Metrics.ValidationAccuracy < minimumTrainingAccuracy ||
                            artifact.Metrics.FalseAcceptRate > 0.01 || artifact.Metrics.FalseRejectRate > 0.10)
                        {
                            StatusText = $"训练未通过联合门禁：准确率 {artifact.Metrics.ValidationAccuracy:P1} / 误识率 {artifact.Metrics.FalseAcceptRate:P2} / 拒识率 {artifact.Metrics.FalseRejectRate:P2}；制品未应用";
                            TrainingResultText = $"未应用模型版本：{artifact.ModelVersion}；制品：{artifact.ArtifactId}；评测准确率 {artifact.Metrics.ValidationAccuracy:P1} / 误识率 {artifact.Metrics.FalseAcceptRate:P2} / 拒识率 {artifact.Metrics.FalseRejectRate:P2}";
                            AppLogger.Warn($"人脸训练未通过准确率门禁：accuracy={artifact.Metrics.ValidationAccuracy:F3}; required={minimumTrainingAccuracy:F2}; artifact={artifact.ArtifactId}");
                            break;
                        }
                        if (!await submission.VerifyArtifactAsync(artifact))
                        {
                            StatusText = "训练制品签名校验失败，已阻止应用。";
                            AppLogger.Error("训练制品签名校验失败，已阻止应用");
                            break;
                        }
                        await UiWork.RunDatabaseAsync(() => ApplyTrainedPrototypes(artifact));
                        // Keep platform adoption and the local model-scoped
                        // history synchronized for this direct submission path.
                        await submission.MarkArtifactAppliedAsync(artifact.ArtifactId);
                        await LoadTrainingHistoryAsync(artifact.ModelVersion);
                        SelectedTrainingHistory = TrainingHistoryOptions.FirstOrDefault(value =>
                            string.Equals(value.ArtifactId, artifact.ArtifactId, StringComparison.OrdinalIgnoreCase));
                        await RefreshPageAsync();
                        StatusText = $"训练完成并已自动应用：准确率 {(artifact.Metrics.ValidationAccuracy * 100):F1}%，" +
                                     $"误识率 {(artifact.Metrics.FalseAcceptRate * 100):F2}%，" +
                                     $"拒识率 {(artifact.Metrics.FalseRejectRate * 100):F2}%，" +
                                     $"{artifact.Prototypes.Count} 位人物原型已更新";
                        TrainingResultText = $"已应用模型版本：{artifact.ModelVersion}；制品：{artifact.ArtifactId}；" +
                                             $"评测：准确率 {(artifact.Metrics.ValidationAccuracy * 100):F1}% / " +
                                             $"误识率 {(artifact.Metrics.FalseAcceptRate * 100):F2}% / " +
                                             $"拒识率 {(artifact.Metrics.FalseRejectRate * 100):F2}%";
                        AppLogger.Info($"人脸训练完成并已应用：{artifact.ArtifactId} acc={artifact.Metrics.ValidationAccuracy:F3} far={artifact.Metrics.FalseAcceptRate:F3}");
                    }
                    break;
                }
                if (status.Status == "failed" || status.Status == "canceled")
                {
                    StatusText = $"训练{status.Status}：{status.Error ?? "无详细信息"}";
                    break;
                }
                StatusText = $"训练进行中：{status.Stage ?? status.Status} {status.Progress}%（已等待 {pollCount} 秒）";
            }
            if (pollCount >= 120) StatusText = $"训练超时（2 分钟），请在工作台 {modelServerUri} 查看";
        }
        catch (Exception exception)
        {
            StatusText = $"训练失败：{exception.Message}";
            AppLogger.Error("人脸训练提交失败", exception);
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    private List<(long FaceId, long PersonId, float[] Embedding, string Split)> ExtractEmbeddings(long snapshotId, string modelVersion)
    {
        var results = new List<(long, long, float[], string)>();
        using var cmd = _database.Command("""
            SELECT DISTINCT target.id, m.person_id, m.split, target.embedding
            FROM face_snapshot_members m
            JOIN detected_faces source ON source.id = m.face_id
            JOIN detected_faces target ON target.image_id=source.image_id
                AND target.ordinal=source.ordinal AND target.model_version=$model
            WHERE m.snapshot_id = $s AND target.embedding IS NOT NULL
            """);
        cmd.Parameters.AddWithValue("$s", snapshotId);
        cmd.Parameters.AddWithValue("$model", modelVersion);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var blob = (byte[])reader.GetValue(3);
            var embedding = new float[blob.Length / 4];
            Buffer.BlockCopy(blob, 0, embedding, 0, blob.Length);
            results.Add((reader.GetInt64(0), reader.GetInt64(1), embedding, reader.GetString(2)));
        }
        return results;
    }

    private List<TrainingNegativeSample> ExtractNegativeEmbeddings(long snapshotId, string modelVersion)
    {
        var results = new List<TrainingNegativeSample>();
        using var cmd = _database.Command("""
            SELECT r.face_id,r.person_id,r.split,target.embedding
            FROM face_snapshot_rejections r
            JOIN detected_faces source ON source.id=r.face_id
            JOIN detected_faces target ON target.image_id=source.image_id
                AND target.ordinal=source.ordinal AND target.model_version=$model
            WHERE r.snapshot_id=$snapshot AND target.embedding IS NOT NULL
            """);
        cmd.Parameters.AddWithValue("$snapshot", snapshotId);
        cmd.Parameters.AddWithValue("$model", modelVersion);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var blob = (byte[])reader.GetValue(3);
            var embedding = new float[blob.Length / sizeof(float)];
            Buffer.BlockCopy(blob, 0, embedding, 0, blob.Length);
            results.Add(new TrainingNegativeSample(reader.GetInt64(0), reader.GetInt64(1), embedding,
                reader.GetString(2), "exclusive"));
        }
        return results;
    }

    private void ApplyTrainedPrototypes(TrainingArtifactData artifact)
    {
        using var clear = _database.Command("DELETE FROM face_person_prototypes WHERE model_version = $mv");
        clear.Parameters.AddWithValue("$mv", artifact.ModelVersion);
        clear.ExecuteNonQuery();
        using var tx = _database.BeginTransaction();
        foreach (var proto in artifact.Prototypes)
        {
            using var cmd = _database.Command("""
                INSERT OR REPLACE INTO face_person_prototypes (person_id, centroid, threshold, sample_count, model_version, updated_utc)
                VALUES ($p, $c, $t, $n, $mv, $u)
                """);
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("$p", proto.PersonId);
            // SQLite cannot bind float[] directly. Store the float32 vector as
            // the same little-endian blob format used by the vision repositories.
            cmd.Parameters.AddWithValue("$c", FloatArrayToBlob(proto.Centroid));
            cmd.Parameters.AddWithValue("$t", proto.Threshold);
            cmd.Parameters.AddWithValue("$n", proto.SampleCount);
            cmd.Parameters.AddWithValue("$mv", artifact.ModelVersion);
            cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        _repository.RecordAgentTrainingArtifact(artifact, artifact.ModelVersion);
        AppLogger.Info($"已应用 {artifact.Prototypes.Count} 个训练原型（{artifact.ArtifactId}）");
    }

    private static byte[] FloatArrayToBlob(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    [RelayCommand]
    private async Task ScanConsistencyAsync()
    {
        StatusText = "正在检查人物归属、人物标签和搜索索引（不训练模型）…";
        try
        {
            var report = await UiWork.RunDatabaseAsync(() => FaceConsistencyService.ScanAndRepair(_database));
            var summary = FaceConsistencyService.BuildTrainingSummary(_database);
            StatusText = $"一致性修复完成：扫描 {report.ScannedImages} 张照片、{report.ScannedFaces} 张人脸；发现 {report.Issues.Count} 项，已修复 {report.Repaired} 项。此操作不训练模型。";
            AppLogger.Info($"人脸一致性报告：{StatusText}\n{summary}");
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusText = $"一致性扫描失败：{exception.Message}";
            AppLogger.Error("人脸一致性扫描失败", exception);
        }
    }

    private async Task CreatePersonAsync()
    {
        AppLogger.Info($"People create-empty invoked; hasName={!string.IsNullOrWhiteSpace(NewPersonName)}");
        if (string.IsNullOrWhiteSpace(NewPersonName))
        {
            StatusText = "请输入人物名称";
            AppLogger.Info("People create-empty stopped because the UI name binding is empty");
            return;
        }
        try
        {
            var name = NewPersonName;
            var person = await UiWork.RunDatabaseAsync(() => _peopleService.Create(name));
            NewPersonName = "";
            var summary = new PersonSummary(person.Id, person.DisplayName, 0, 0);
            var insertionIndex = 0;
            while (insertionIndex < People.Count &&
                   StringComparer.CurrentCultureIgnoreCase.Compare(People[insertionIndex].DisplayName, summary.DisplayName) < 0)
                insertionIndex++;
            People.Insert(insertionIndex, summary);
            CandidateTargetPerson = summary;
            AppLogger.Info($"People create-empty completed; person={person.Id}; dropdownCount={People.Count}");
            StatusText = $"已创建人物“{person.DisplayName}”";
        }
        catch (Exception exception)
        {
            StatusText = $"创建人物失败：{exception.Message}";
        }
    }

    public Task CreateEmptyPersonFromUiAsync() => CreatePersonAsync();

    public async Task CreatePersonWithFacesAsync(string name, IReadOnlyCollection<UnassignedFaceViewModel> faces)
    {
        if (string.IsNullOrWhiteSpace(name)) { StatusText = "请输入人物名称"; return; }
        var expandedFaces = ExpandOperationFaces(faces).ToArray();
        var sourceByFace = expandedFaces.GroupBy(face => face.FaceId)
            .ToDictionary(group => group.Key, group => group.Select(face => face.Record.SuggestedPersonId).FirstOrDefault());
        var faceIds = expandedFaces.Select(face => face.FaceId).Distinct().ToArray();
        RemoveFacesFromCurrentView(faceIds);
        StatusText = faceIds.Length == 0 ? "正在创建人物…" : $"正在创建人物并归入 {faceIds.Length} 张人脸…";
        try
        {
            var person = await UiWork.RunDatabaseAsync("People.CreateAndAssignFaces",
                () =>
                {
                    var created = _peopleService.CreateAndAssignFaces(name, faceIds);
                    foreach (var fid in faceIds)
                    {
                        var predicted = sourceByFace.GetValueOrDefault(fid);
                        AuditFaceAction("create_person", fid, created.Id, predicted,
                            predicted is not null && predicted != created.Id ? "false_positive" : null);
                    }
                    return created;
                });
            await RefreshPeopleDropdownAsync(person.Id);
            StatusText = faceIds.Length == 0
                ? $"已创建人物“{person.DisplayName}”"
                : $"已创建人物“{person.DisplayName}”并归入 {faceIds.Length} 张人脸";
        }
        catch (Exception exception)
        {
            AppLogger.Error($"创建人物并归类失败：faceCount={faceIds.Length}", exception);
            await RefreshAsync();
            StatusText = $"创建人物并归类失败，界面已恢复：{exception.Message}";
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
        var result = await UiWork.RunDatabaseAsync(() => _suggestionEngine.Suggest(modelVersion: SelectedFaceModelVersion));
        await RefreshAsync();
        StatusText = $"候选匹配完成：检查 {result.Considered} 张，新增建议 {result.Suggested}；" +
                     $"暂缓：基础阈值 {result.BelowThreshold}、低质量 {result.LowQualityDeferred}、人物歧义 {result.AmbiguousDeferred}、负样本冲突 {result.NegativeConflictDeferred}；" +
                     $"多原型人物 {result.MultiPrototypePeople} 位，负样本原型 {result.NegativeSamples} 个";
    }

    [RelayCommand(CanExecute = nameof(CanOrganizeFaces))]
    private async Task OrganizeFacesAsync(CancellationToken cancellationToken = default)
    {
        if (!await _organizeGate.WaitAsync(0, cancellationToken))
        {
            StatusText = "自动归类正在执行，请勿重复点击；当前进度会持续显示";
            ShowOrganizeProgress = true;
            OrganizeProgressText = "已有一个自动归类任务正在执行；本次重复点击已忽略";
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        IsOrganizing = true;
        ShowOrganizeProgress = true;
        using var organizeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Interlocked.Exchange(ref _organizeCancellation, organizeCancellation)?.Cancel();
        cancellationToken = organizeCancellation.Token;
        var organizingModelVersion = SelectedFaceModelVersion;
        try
        {
            ProgressValue = 5;
            StatusText = "正在使用最新标准更新自动归类（1/3：匹配已命名人物，5%）";
            OrganizeProgressText = StatusText;
            var suggestionProgress = new Progress<(int Completed, int Total)>(value =>
            {
                var percent = value.Total == 0 ? 35 : 5 + value.Completed * 30d / value.Total;
                ProgressValue = percent;
                StatusText = $"正在使用最新标准更新自动归类（1/3：匹配已命名人物 {value.Completed}/{value.Total}，{percent:F0}%）";
                OrganizeProgressText = StatusText;
            });
            var suggestions = await UiWork.RunDatabaseAsync("People.AutoOrganize.Suggest",
                () => _suggestionEngine.Suggest(progress: suggestionProgress, modelVersion: organizingModelVersion), cancellationToken);

            ProgressValue = 35;
            StatusText = $"正在使用最新标准更新自动归类（2/3：重建待命名人物组，35%；已检查 {suggestions.Considered} 张脸）";
            OrganizeProgressText = StatusText;
            var clusterProgress = new Progress<(string Stage, int Completed, int Total)>(value =>
            {
                var ratio = value.Total <= 0 ? 0 : Math.Clamp(value.Completed / (double)value.Total, 0, 1);
                var percent = 35 + ratio * 43;
                ProgressValue = percent;
                OrganizeProgressText = $"正在使用最新标准更新自动归类（2/3：{value.Stage} {value.Completed}/{value.Total}，{percent:F0}%）";
                StatusText = OrganizeProgressText;
            });
            var clusters = await UiWork.RunDatabaseAsync("People.AutoOrganize.Cluster",
                () => _unknownFaceClusterer.Rebuild(suggestions.AppliedThreshold, progress: clusterProgress,
                    modelVersion: organizingModelVersion), cancellationToken);

            ProgressValue = 80;
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(SelectedFaceModelVersion, organizingModelVersion, StringComparison.OrdinalIgnoreCase))
                throw new OperationCanceledException(cancellationToken);
            StatusText = $"正在使用最新标准更新自动归类（3/3：同步界面，80%；共 {clusters.ClusterCount} 个候选组）";
            OrganizeProgressText = StatusText;
            await RefreshAsync();
            ProgressValue = 100;
            StatusText = $"自动归类完成（100%）：新增推荐 {suggestions.Suggested} 张，待命名 {clusters.ClusterCount} 组；" +
                         $"暂缓低质量 {suggestions.LowQualityDeferred}、人物歧义 {suggestions.AmbiguousDeferred}、负样本冲突 {suggestions.NegativeConflictDeferred}；" +
                         $"已学习 {suggestions.MultiPrototypePeople} 位多原型人物和 {suggestions.NegativeSamples} 个负样本原型；用时 {stopwatch.Elapsed.TotalSeconds:F1} 秒";
            OrganizeProgressText = StatusText;
            AppLogger.Info($"People.AutoOrganize completed in {stopwatch.ElapsedMilliseconds} ms; " +
                           $"considered={suggestions.Considered}; suggested={suggestions.Suggested}; clusters={clusters.ClusterCount}");
        }
        catch (OperationCanceledException)
        {
            StatusText = "自动归类已取消；已经保存的人工标记不受影响";
            OrganizeProgressText = StatusText;
        }
        catch (Exception exception)
        {
            StatusText = $"自动归类失败：{exception.Message}";
            OrganizeProgressText = StatusText;
            AppLogger.Error("People.AutoOrganize failed", exception);
        }
        finally
        {
            Interlocked.CompareExchange(ref _organizeCancellation, null, organizeCancellation);
            IsOrganizing = false;
            _organizeGate.Release();
        }
    }

    private bool CanOrganizeFaces() => !IsOrganizing;

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
        // Opening/closing the original is read-only. Do not rebuild the face
        // collection here: that resets virtualization, scroll offset and the
        // user's current category/position. Mutating face commands perform
        // their own targeted refresh.
        AppLogger.Info($"People face preview closed without refresh; face={face.FaceId}; image={face.Record.ImageId}");
    }

    public async Task MoveFaceToPersonAsync(UnassignedFaceViewModel face, PersonSummary person)
        => await MoveFacesToPersonAsync([face], person);

    public async Task ConfirmAutomaticSuggestionAsync(UnassignedFaceViewModel face)
    {
        if (face.Record.SuggestedPersonId is not long personId) return;
        face.IsAutomaticSuggestion = false;
        if (DisplayedCollectionKind is DisplayedFaceCollectionKind.Unassigned
            or DisplayedFaceCollectionKind.CandidateCluster
            or DisplayedFaceCollectionKind.SuggestedGroup)
            RemoveFacesFromCurrentView([face.FaceId]);
        StatusText = "正在后台保存人工确认…";
        try
        {
            await UiWork.RunDatabaseAsync("ConfirmAutomaticSuggestion", () =>
            {
                _peopleService.ConfirmFace(face.FaceId, personId);
                AuditFaceAction("confirm", face.FaceId, personId, face.Record.SuggestedPersonId);
            });
            StatusText = "已确认该人脸，后续自动归类会将其作为人物参照";
        }
        catch (Exception exception)
        {
            face.IsAutomaticSuggestion = true;
            RefreshDisplayedFaces();
            StatusText = $"确认人脸失败：{exception.Message}";
            AppLogger.Error($"确认自动人脸建议失败：{face.FaceId}", exception);
        }
    }

    public async Task RejectAutomaticSuggestionAsync(UnassignedFaceViewModel face)
    {
        if (face.Record.SuggestedPersonId is not long personId) return;
        RemoveFacesFromCurrentView([face.FaceId]);
        StatusText = "已从当前人物移除，正在后台保存拒绝标记…";
        try
        {
            await UiWork.RunDatabaseAsync("RejectAutomaticSuggestion", () =>
            {
                _peopleService.MarkNotThisPerson(face.FaceId, personId);
                AuditFaceAction("reject", face.FaceId, null, personId, "false_positive");
            });
            StatusText = "已标记不是此人；请选择正确人物，或保留在待分类";
        }
        catch (Exception exception)
        {
            RefreshDisplayedFaces();
            StatusText = $"拒绝自动建议失败，界面正在恢复：{exception.Message}";
            AppLogger.Error($"拒绝自动人脸建议失败：{face.FaceId}", exception);
        }
    }

    public async Task MoveFacesToPersonAsync(IReadOnlyCollection<UnassignedFaceViewModel> faces, PersonSummary person)
    {
        var operationId = Interlocked.Increment(ref _faceMoveOperationId);
        var stopwatch = Stopwatch.StartNew();
        var expandedFaces = ExpandOperationFaces(faces).ToArray();
        var sourceByFace = expandedFaces.GroupBy(face => face.FaceId)
            .ToDictionary(group => group.Key, group => group.Select(face => face.Record.SuggestedPersonId).FirstOrDefault());
        var faceIds = expandedFaces.Select(face => face.FaceId).Distinct().ToArray();
        if (faceIds.Length == 0) return;
        RemoveFacesFromCurrentView(faceIds);
        AppLogger.Info($"FaceMove #{operationId}: UI removed {faceIds.Length} faces in {stopwatch.ElapsedMilliseconds} ms; target={person.Id}");
        StatusText = $"正在后台将 {faceIds.Length} 张人脸移到“{person.DisplayName}”…";
        try
        {
            var databaseStart = stopwatch.ElapsedMilliseconds;
            var backgroundCompleted = Stopwatch.GetTimestamp();
            var result = await UiWork.RunDatabaseAsync($"FaceMove#{operationId}", () =>
            {
                var value = _peopleService.MoveFaces(faceIds, person.Id);
                foreach (var fid in faceIds)
                    AuditFaceAction("reclassify", fid, person.Id, sourceByFace.GetValueOrDefault(fid),
                        sourceByFace.GetValueOrDefault(fid) is not null ? "false_positive" : null);
                backgroundCompleted = Stopwatch.GetTimestamp();
                return value;
            });
            var uiResumeMilliseconds = Stopwatch.GetElapsedTime(backgroundCompleted).TotalMilliseconds;
            AppLogger.Info($"FaceMove #{operationId}: database batch completed in {stopwatch.ElapsedMilliseconds - databaseStart} ms; changed={result.Changed}; already-assigned={result.AlreadyAssigned}");
            if (uiResumeMilliseconds >= 50) AppLogger.Info($"FaceMove #{operationId}: UI resume delay={uiResumeMilliseconds:F0} ms");
            var dropdownStart = stopwatch.ElapsedMilliseconds;
            await RefreshPeopleDropdownAsync(person.Id);
            AppLogger.Info($"FaceMove #{operationId}: dropdown refreshed in {stopwatch.ElapsedMilliseconds - dropdownStart} ms; total={stopwatch.ElapsedMilliseconds} ms");
            StatusText = result.AlreadyAssigned == 0
                ? $"已将 {result.Changed} 张人脸移到“{person.DisplayName}”"
                : $"已同步到“{person.DisplayName}”：新移动 {result.Changed} 张，原本已归类 {result.AlreadyAssigned} 张";
        }
        catch (Exception exception)
        {
            AppLogger.Error($"批量移动人脸到人物失败：{string.Join(',', faceIds)}", exception);
            RefreshDisplayedFaces();
            StatusText = $"移动到“{person.DisplayName}”失败，界面正在恢复：{exception.Message}";
        }
    }

    private void AuditFaceAction(string action, long faceId, long? personId, long? sourcePersonId,
        string? errorType = null)
    {
        try
        {
            var coFaces = new List<long>();
            var coPersons = new List<long>();
            if (sourcePersonId is long src)
                using (var cmd = _database.Command("""
                    SELECT fa.face_id, fa.person_id
                    FROM face_person_assignments fa
                    WHERE fa.person_id = $sp AND fa.state = 'confirmed' AND fa.face_id != $f
                    LIMIT 20
                    """))
                {
                    cmd.Parameters.AddWithValue("$sp", src);
                    cmd.Parameters.AddWithValue("$f", faceId);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        coFaces.Add(reader.GetInt64(0));
                        coPersons.Add(reader.GetInt64(1));
                    }
                }
            FaceActionLogService.Record(_database, action, faceId, personId, sourcePersonId, coFaces, coPersons,
                null, SelectedFaceModelVersion ?? "unknown", errorType);
            EnsureImageDescriptionTag(faceId, personId);
        }
        catch (Exception exception)
        {
            AppLogger.Error($"记录人脸审计日志失败：{action} face={faceId}", exception);
        }
    }

    private void EnsureImageDescriptionTag(long faceId, long? personId)
    {
        if (personId is not long pid) return;
        try
        {
            using (var imageCmd = _database.Command("SELECT image_id FROM detected_faces WHERE id = $f"))
            {
                imageCmd.Parameters.AddWithValue("$f", faceId);
                var imageId = imageCmd.ExecuteScalar();
                if (imageId is null) return;
                using (var nameCmd = _database.Command("SELECT display_name FROM people WHERE id = $p"))
                {
                    nameCmd.Parameters.AddWithValue("$p", pid);
                    var name = nameCmd.ExecuteScalar() as string;
                    if (string.IsNullOrEmpty(name)) return;
                    var tag = $"人物：{name}";
                    var record = _database.GetImage((long)imageId);
                    if (record is null) return;
                    var textIndex = new Picer.Core.Search.MediaTextIndexRepository(_database);
                    textIndex.UpsertImage(record, [record.Path, record.Camera, record.Gps, tag, name], $"person-tag:{pid}");
                }
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error($"更新照片人物标记失败：face={faceId}", exception);
        }
    }

    public async Task MoveFaceToUnassignedAsync(UnassignedFaceViewModel face)
        => await MoveFacesToUnassignedAsync([face]);

    public async Task MoveFacesToUnassignedAsync(IReadOnlyCollection<UnassignedFaceViewModel> faces)
    {
        var expandedFaces = ExpandOperationFaces(faces).ToArray();
        var sourceByFace = expandedFaces.GroupBy(face => face.FaceId)
            .ToDictionary(group => group.Key, group => group.Select(face => face.Record.SuggestedPersonId).FirstOrDefault());
        var faceIds = expandedFaces.Select(face => face.FaceId).Distinct().ToArray();
        if (faceIds.Length == 0) return;
        RemoveFacesFromCurrentView(faceIds);
        StatusText = $"正在后台将 {faceIds.Length} 张人脸移到待分类…";
        await UiWork.RunDatabaseAsync("MoveFacesToUnassigned", () =>
        {
            foreach (var faceId in faceIds)
            {
                _peopleService.MoveFaceToUnassigned(faceId);
                    AuditFaceAction("move_to_unassigned", faceId, null, sourceByFace.GetValueOrDefault(faceId),
                        sourceByFace.GetValueOrDefault(faceId) is not null ? "false_positive" : "manual_unassignment");
            }
        });
        StatusText = $"已将 {faceIds.Length} 张人脸移到待分类";
    }

    public async Task IgnoreFaceAsync(UnassignedFaceViewModel face)
        => await IgnoreFacesAsync([face]);

    public async Task IgnoreFacesAsync(IReadOnlyCollection<UnassignedFaceViewModel> faces)
    {
        var expandedFaces = ExpandOperationFaces(faces).ToArray();
        var sourceByFace = expandedFaces.GroupBy(face => face.FaceId)
            .ToDictionary(group => group.Key, group => group.Select(face => face.Record.SuggestedPersonId).FirstOrDefault());
        var faceIds = expandedFaces.Select(face => face.FaceId).Distinct().ToArray();
        if (faceIds.Length == 0) return;
        RemoveFacesFromCurrentView(faceIds);
        StatusText = $"正在将 {faceIds.Length} 张人脸移到不分类…";
        var changed = 0;
        try
        {
            changed = (await UiWork.RunDatabaseAsync("IgnoreFaces", () =>
            {
                var result = _peopleService.IgnoreFaces(faceIds);
                foreach (var faceId in faceIds)
                    AuditFaceAction("ignore", faceId, null, sourceByFace.GetValueOrDefault(faceId),
                        sourceByFace.GetValueOrDefault(faceId) is not null ? "false_positive" : "unknown_person");
                return result;
            })).Changed;
            var verified = await UiWork.RunDatabaseAsync(() =>
                _repository.GetIgnoredFaces(5000).Select(face => face.FaceId).ToHashSet());
            var missing = faceIds.Where(faceId => !verified.Contains(faceId)).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException($"数据库复核失败，仍有 {missing.Length} 张人脸未进入不分类");
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
                RemoveFacesFromCurrentView(faceIds);
                StatusText = $"所选 {faceIds.Length} 张人脸已是不分类；界面已重新同步";
            }
            else StatusText = $"移动到不分类失败：{exception.Message}";
        }
    }

    public async Task IgnoreCandidateClusterAsync(CandidateClusterViewModel cluster)
    {
        var faceIds = cluster.Faces.Select(face => face.FaceId).Distinct().ToArray();
        if (faceIds.Length == 0) return;
        if (SelectedCandidateCluster?.Record.Id == cluster.Record.Id)
        {
            RemoveFacesFromCurrentView(faceIds);
            SelectedCandidateCluster = null;
        }
        CandidateClusters.Remove(cluster);
        StatusText = $"候选人物已从界面移除，正在后台标记 {faceIds.Length} 张人脸为不分类…";
        try
        {
            var result = await UiWork.RunDatabaseAsync("IgnoreCandidateCluster", () => _peopleService.IgnoreFaces(faceIds));
            StatusText = $"已将整组 {result.Requested} 张人脸标记为不分类";
        }
        catch (Exception exception)
        {
            AppLogger.Error($"整组标记候选人物为不分类失败：{cluster.Record.Id}", exception);
            StatusText = $"整组标记失败，正在恢复界面：{exception.Message}";
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private Task ShowUnassignedFacesAsync() => ShowUnassignedFacesCoreAsync(collapseBursts: true);

    private async Task ShowUnassignedFacesCoreAsync(bool collapseBursts)
    {
        var stopwatch = Stopwatch.StartNew();
        AppLogger.Info("People pending-faces load started");
        _collectionLoadCancellation?.Cancel();
        SelectedPerson = null;
        SelectedCandidateCluster = null;
        DisplayedCollectionKind = DisplayedFaceCollectionKind.Unassigned;
        DisplayedFaces.Clear();
        DisplayedFacesTitle = "待分类人脸 · 正在加载…";
        var cancellation = _collectionLoadCancellation = new CancellationTokenSource();
        try
        {
            var snapshot = await UiWork.RunDatabaseAsync(() => (
                Records: _repository.OrderByFeature(_repository.GetUnassignedFaces(5000, SelectedFaceModelVersion)).ToArray(),
                Counts: _repository.GetFaceWorkQueueCounts(SelectedFaceModelVersion)), cancellation.Token);
            var records = snapshot.Records;
            if (DisplayedCollectionKind != DisplayedFaceCollectionKind.Unassigned) return;
            // A model switch should paint the pending faces immediately. The
            // burst grouping query is useful for the settled view, but it is
            // not allowed to delay the first render of a newly selected model.
            var items = collapseBursts
                ? await BuildBurstAwareFaceItemsAsync(records, cancellation.Token)
                : records.Select(CreateFaceItem).ToArray();
            if (DisplayedCollectionKind != DisplayedFaceCollectionKind.Unassigned) return;
            UnassignedFaces.Clear();
            await UiWork.AddInBatchesAsync(items, item =>
            {
                UnassignedFaces.Add(item); DisplayedFaces.Add(item);
                LoadDisplayedFaceThumbnailAsync(item, cancellation.Token).Forget();
                DisplayedFacesTitle = $"待分类人脸 · {DisplayedFaces.Count}/{items.Count}";
            }, cancellation.Token, batchSize: 30);
            UnassignedSummary = $"待处理人脸 · {snapshot.Counts.Total}（待确认 {snapshot.Counts.Suggested} / 待命名 {snapshot.Counts.Candidate} / 待分类 {snapshot.Counts.Unassigned}）";
            DisplayedFacesTitle = records.Length < snapshot.Counts.Total
                ? $"待处理人脸（已显示 {records.Length} / 共 {snapshot.Counts.Total}）"
                : $"待处理人脸（{snapshot.Counts.Total}）";
            AppLogger.Info($"People pending-faces load completed; records={records.Length}; rendered={DisplayedFaces.Count}; elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException) { AppLogger.Info($"People pending-faces load canceled; elapsedMs={stopwatch.ElapsedMilliseconds}"); }
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
            var records = await UiWork.RunDatabaseAsync(() =>
                _repository.OrderByFeature(_repository.GetIgnoredFaces(5000)).ToArray(), cancellation.Token);
            if (DisplayedCollectionKind != DisplayedFaceCollectionKind.Ignored) return;
            var items = await BuildBurstAwareFaceItemsAsync(records, cancellation.Token);
            if (DisplayedCollectionKind != DisplayedFaceCollectionKind.Ignored) return;
            await UiWork.AddInBatchesAsync(items, item =>
            {
                DisplayedFaces.Add(item);
                LoadDisplayedFaceThumbnailAsync(item, cancellation.Token).Forget();
                DisplayedFacesTitle = $"不分类 · {DisplayedFaces.Count}/{items.Count} 项";
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
        if (string.IsNullOrWhiteSpace(NewPersonName))
        {
            StatusText = "请输入人物名称";
            return;
        }
        if (SelectedCandidateCluster is null)
        {
            await CreatePersonAsync();
            return;
        }
        var name = NewPersonName;
        var faces = SelectedCandidateCluster.Faces.ToArray();
        try
        {
            var person = await UiWork.RunDatabaseAsync(() =>
                _peopleService.CreateAndAssignFaces(name, faces.Select(face => face.FaceId)));
            NewPersonName = "";
            await RefreshAsync();
            StatusText = $"已将候选组确认为“{person.DisplayName}”，可继续逐脸纠错";
        }
        catch (Exception exception)
        {
            StatusText = $"创建人物失败：{exception.Message}";
        }
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
        var faceIds = SelectedCandidateCluster.Faces.Select(face => face.FaceId).Distinct().ToArray();
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
        if (_suppressCollectionSelectionCallbacks || value is null) return;
        _collectionLoadCancellation?.Cancel();
        _personLoadCancellation?.Cancel();
        _clusterLoadCancellation?.Cancel();
        SelectedClusterFaces.Clear();
        var cancellation = _clusterLoadCancellation = new CancellationTokenSource();
        SetOtherCollectionSelectionsToNull(clearPerson: true, clearCandidate: false, clearSuggested: true);
        DisplayedCollectionKind = DisplayedFaceCollectionKind.CandidateCluster;
        DisplayedFaces.Clear();
        DisplayedFacesTitle = $"{value.Summary} · 正在加载…";
        _ = LoadCandidateClusterAsync(value, cancellation.Token);
    }

    partial void OnSelectedSuggestedPersonGroupChanged(SuggestedPersonGroupViewModel? value)
    {
        if (_suppressCollectionSelectionCallbacks || value is null) return;
        _collectionLoadCancellation?.Cancel();
        _personLoadCancellation?.Cancel();
        _clusterLoadCancellation?.Cancel();
        DisplayedFaces.Clear();
        SetOtherCollectionSelectionsToNull(clearPerson: true, clearCandidate: true, clearSuggested: false);
        DisplayedCollectionKind = DisplayedFaceCollectionKind.SuggestedGroup;
        DisplayedFacesTitle = $"推荐为 {value.Record.PersonName} · 正在加载…";
        var cancellation = _collectionLoadCancellation = new CancellationTokenSource();
        LoadSuggestedPersonGroupAsync(value, cancellation.Token).Forget();
    }

    private async Task LoadSuggestedPersonGroupAsync(SuggestedPersonGroupViewModel group, CancellationToken cancellationToken)
    {
        try
        {
            var faces = await UiWork.RunDatabaseAsync(() =>
                _repository.OrderByFeature(group.Faces).ToArray(), cancellationToken);
            var items = await BuildBurstAwareFaceItemsAsync(faces, cancellationToken);
            await UiWork.AddInBatchesAsync(items, item =>
            {
                DisplayedFaces.Add(item);
                LoadDisplayedFaceThumbnailAsync(item, cancellationToken).Forget();
                DisplayedFacesTitle = $"推荐为 {group.Record.PersonName} · {DisplayedFaces.Count}/{items.Count}";
            }, cancellationToken, batchSize: 30);
            DisplayedFacesTitle = $"推荐为 {group.Record.PersonName}（{DisplayedFaces.Count}）";
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    private async Task ConfirmSuggestedPersonGroupAsync()
    {
        if (SelectedSuggestedPersonGroup is not { } group) { StatusText = "请先选择一个待确认推荐组"; return; }
        var faceIds = group.Faces.Select(face => face.FaceId).Distinct().ToArray();
        StatusText = $"正在确认推荐组“{group.Record.PersonName}”的 {faceIds.Length} 张人脸…";
        await UiWork.RunDatabaseAsync("People.ConfirmSuggestedGroup",
            () => _peopleService.MoveFaces(faceIds, group.Record.PersonId));
        RemoveFacesFromCurrentView(faceIds);
        SuggestedPersonGroups.Remove(group);
        SelectedSuggestedPersonGroup = null;
        StatusText = $"已整组确认为“{group.Record.PersonName}”：{faceIds.Length} 张人脸";
    }

    private async Task LoadCandidateClusterAsync(CandidateClusterViewModel cluster, CancellationToken cancellationToken)
    {
        try
        {
            var faces = await UiWork.RunDatabaseAsync(() =>
                _repository.OrderByFeature(cluster.Faces).ToArray(), cancellationToken);
            var items = await BuildBurstAwareFaceItemsAsync(faces, cancellationToken);
            await UiWork.AddInBatchesAsync(items, item =>
            {
                SelectedClusterFaces.Add(item);
                DisplayedFaces.Add(item);
                LoadDisplayedFaceThumbnailAsync(item, cancellationToken).Forget();
                DisplayedFacesTitle = $"候选人物 · {DisplayedFaces.Count} / {items.Count} 项";
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
        _organizeGate.Dispose();
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
        var path = await GetFaceThumbnailAsync(item.Record);
        if (path is not null && UnassignedFaces.Contains(item)) item.FaceThumbnailPath = path;
    }


    private async Task LoadClusterThumbnailAsync(CandidateClusterViewModel item)
    {
        var representative = item.Faces.FirstOrDefault();
        if (representative is null) return;
        var path = await GetFaceThumbnailAsync(representative);
        if (path is not null && CandidateClusters.Contains(item)) item.RepresentativeThumbnailPath = path;
    }

    private async Task LoadClusterMemberThumbnailAsync(UnassignedFaceViewModel item)
    {
        var path = await GetFaceThumbnailAsync(item.Record);
        if (path is not null && SelectedClusterFaces.Contains(item)) item.FaceThumbnailPath = path;
    }

    partial void OnSelectedPersonChanged(PersonSummary? value)
    {
        if (_suppressPersonSelectionLoad || _suppressCollectionSelectionCallbacks || value is null) return;
        _collectionLoadCancellation?.Cancel();
        _clusterLoadCancellation?.Cancel();
        _personLoadCancellation?.Cancel();
        _personLoadCancellation?.Dispose();
        AssignedFaces.Clear();
        PersonVideoFaces.Clear();
        var cancellation = _personLoadCancellation = new CancellationTokenSource();
        SetOtherCollectionSelectionsToNull(clearPerson: false, clearCandidate: true, clearSuggested: true);
        DisplayedCollectionKind = DisplayedFaceCollectionKind.NamedPerson;
        DisplayedFacesTitle = $"{value.DisplayName} · {CurrentClassificationModelLabel()} · 正在加载…";
        DisplayedFaces.Clear();
        AppLogger.Info($"People named-person selection: person={value.Id}");
        _ = LoadPersonFacesAsync(value, cancellation.Token);
    }

    private void SetOtherCollectionSelectionsToNull(bool clearPerson, bool clearCandidate, bool clearSuggested)
    {
        _suppressCollectionSelectionCallbacks = true;
        try
        {
            if (clearPerson) SelectedPerson = null;
            if (clearCandidate) SelectedCandidateCluster = null;
            if (clearSuggested) SelectedSuggestedPersonGroup = null;
        }
        finally
        {
            _suppressCollectionSelectionCallbacks = false;
        }
    }

    private async Task LoadPersonFacesAsync(PersonSummary person, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await UiWork.RunDatabaseAsync(() => (
                // 人工确认的人脸是跨模型共享的真值；模型切换只改变自动归类工作集，
                // 不应把已命名人物拆成互不相见的列表。
                Photos: _repository.OrderByFeature(_repository.GetPersonFaces(person.Id)).ToArray(),
                Videos: AppServices.VideoFaceTimeline?.GetPersonFaces(person.Id).ToArray() ?? []), cancellationToken);
            var faces = snapshot.Photos;
            if (cancellationToken.IsCancellationRequested || SelectedPerson?.Id != person.Id) return;
            var items = await BuildBurstAwareFaceItemsAsync(faces, cancellationToken);
            await UiWork.AddInBatchesAsync(items, item =>
                {
                    AssignedFaces.Add(item);
                    DisplayedFaces.Add(item);
                    LoadDisplayedFaceThumbnailAsync(item, cancellationToken).Forget();
                DisplayedFacesTitle = $"{person.DisplayName} · {AssignedFaces.Count} / {items.Count} 项";
                }, cancellationToken);
                DisplayedFacesTitle = $"{person.DisplayName} · {AssignedFaces.Count} 张人脸 · {CurrentClassificationModelLabel()}";
            foreach (var record in snapshot.Videos)
            {
                var item = new VideoFaceItemViewModel(record);
                PersonVideoFaces.Add(item);
                _ = item.LoadThumbnailAsync();
            }
            if (PersonVideoFaces.Count > 0) DisplayedFacesTitle += $" + {PersonVideoFaces.Count} 张视频人脸";
            AppLogger.Info($"People named-person loaded: person={person.Id}; photos={AssignedFaces.Count}; videos={PersonVideoFaces.Count}");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            AppLogger.Error($"People named-person load failed: person={person.Id}", exception);
            if (SelectedPerson?.Id == person.Id)
            {
                DisplayedFacesTitle = $"{person.DisplayName} · 加载失败";
                StatusText = $"加载人物“{person.DisplayName}”失败，详细信息已写入日志";
            }
        }
    }

    private async Task LoadAssignedFaceThumbnailAsync(UnassignedFaceViewModel item)
    {
        var path = await GetFaceThumbnailAsync(item.Record);
        if (path is not null && AssignedFaces.Contains(item)) item.FaceThumbnailPath = path;
    }

    private UnassignedFaceViewModel CreateFaceItem(UnassignedFaceRecord record) =>
        new(record, _faceThumbnails.GetCachedPath(record.FaceId) ?? GetSafePreviewPath(record));

    private async Task<IReadOnlyList<UnassignedFaceViewModel>> BuildBurstAwareFaceItemsAsync(
        IReadOnlyList<UnassignedFaceRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0) return [];
        var groups = await UiWork.RunDatabaseAsync("People.BurstGroups.Load", () =>
            new BurstGroupService(_database).GetGroupsForImages(records.Select(face => face.ImageId)), cancellationToken);
        var groupByImage = groups.Values.SelectMany(group => group.ImageIds.Select(imageId => (imageId, group)))
            .ToDictionary(value => value.imageId, value => value.group);
        var recordsByImage = records.GroupBy(face => face.ImageId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var handled = new HashSet<long>();
        var result = new List<UnassignedFaceViewModel>(records.Count);
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!groupByImage.TryGetValue(record.ImageId, out var group))
            {
                result.Add(CreateFaceItem(record));
                continue;
            }
            if (!handled.Add(group.Id)) continue;
            var members = group.ImageIds
                .Where(recordsByImage.ContainsKey)
                .SelectMany(imageId => recordsByImage[imageId])
                .Select(CreateFaceItem).ToArray();
            if (members.Select(face => face.Record.ImageId).Distinct().Count() < 2)
            {
                result.AddRange(members);
                continue;
            }
            members[0].ConfigureBurst(members, ToggleFaceBurst);
            result.Add(members[0]);
        }
        return result;
    }

    private void ToggleFaceBurst(UnassignedFaceViewModel representative)
    {
        var index = DisplayedFaces.IndexOf(representative);
        if (index < 0) return;
        if (representative.IsBurstExpanded)
        {
            foreach (var member in representative.BurstOperationFaces.Skip(1)) DisplayedFaces.Remove(member);
            representative.IsBurstExpanded = false;
            return;
        }
        representative.IsBurstExpanded = true;
        foreach (var member in representative.BurstOperationFaces.Skip(1).Reverse())
        {
            DisplayedFaces.Insert(index + 1, member);
            LoadDisplayedFaceThumbnailAsync(member).Forget();
        }
    }

    private static UnassignedFaceViewModel[] ExpandOperationFaces(IEnumerable<UnassignedFaceViewModel> faces) =>
        faces.SelectMany(face => face.BurstOperationFaces).DistinctBy(face => face.FaceId).ToArray();

    private static string? GetSafePreviewPath(UnassignedFaceRecord record) =>
        !string.IsNullOrWhiteSpace(record.ThumbnailPath) && File.Exists(record.ThumbnailPath)
            ? record.ThumbnailPath
            : null;

    private void RefreshDisplayedFaces()
    {
        if (SelectedPerson is not null) OnSelectedPersonChanged(SelectedPerson);
        else if (SelectedCandidateCluster is not null) OnSelectedCandidateClusterChanged(SelectedCandidateCluster);
        else if (DisplayedFacesTitle.StartsWith("不分类", StringComparison.Ordinal)) _ = ShowIgnoredFacesAsync();
        else ShowUnassignedFacesAsync().Forget();
    }

    private void RemoveFacesFromCurrentView(IReadOnlyCollection<long> faceIds)
    {
        var ids = faceIds.ToHashSet();
        foreach (var item in DisplayedFaces.Where(item => ids.Contains(item.FaceId)).ToArray()) DisplayedFaces.Remove(item);
        foreach (var item in AssignedFaces.Where(item => ids.Contains(item.FaceId)).ToArray()) AssignedFaces.Remove(item);
        foreach (var item in SelectedClusterFaces.Where(item => ids.Contains(item.FaceId)).ToArray()) SelectedClusterFaces.Remove(item);
        foreach (var item in UnassignedFaces.Where(item => ids.Contains(item.FaceId)).ToArray()) UnassignedFaces.Remove(item);
        foreach (var cluster in CandidateClusters.ToArray())
        {
            cluster.RemoveFaces(ids);
            if (cluster.FaceCount >= 2) continue;
            CandidateClusters.Remove(cluster);
            if (SelectedCandidateCluster?.Record.Id == cluster.Record.Id) SelectedCandidateCluster = null;
        }
        foreach (var group in SuggestedPersonGroups.ToArray())
        {
            group.RemoveFaces(ids);
            if (group.FaceCount > 0) continue;
            SuggestedPersonGroups.Remove(group);
            if (SelectedSuggestedPersonGroup?.Record.PersonId == group.Record.PersonId)
                SelectedSuggestedPersonGroup = null;
        }
        if (SelectedDisplayedFace is not null && ids.Contains(SelectedDisplayedFace.FaceId)) SelectedDisplayedFace = null;
        DisplayedFacesTitle = DisplayedCollectionKind switch
        {
            DisplayedFaceCollectionKind.NamedPerson => $"{SelectedPerson?.DisplayName ?? "人物"} · {DisplayedFaces.Count} 张人脸 · {CurrentClassificationModelLabel()}",
            DisplayedFaceCollectionKind.CandidateCluster => $"候选人物 · {DisplayedFaces.Count} 张人脸",
            DisplayedFaceCollectionKind.SuggestedGroup => SelectedSuggestedPersonGroup is { } group
                ? $"推荐为 {group.Record.PersonName}（{DisplayedFaces.Count}）"
                : "待确认推荐组已处理完毕",
            DisplayedFaceCollectionKind.Unassigned => $"待分类人脸（{DisplayedFaces.Count}）",
            DisplayedFaceCollectionKind.Ignored => $"不分类（{DisplayedFaces.Count}）",
            _ => DisplayedFacesTitle
        };
    }

    private async Task RefreshPeopleDropdownAsync(long createdPersonId)
    {
        var selectedId = SelectedPerson?.Id;
        var values = await UiWork.RunDatabaseAsync(() => _peopleService.GetAll().ToArray());
        _suppressPersonSelectionLoad = true;
        try
        {
            People.Clear();
            foreach (var person in values) People.Add(person);
            SelectedPerson = selectedId is long id ? People.FirstOrDefault(person => person.Id == id) : null;
            CandidateTargetPerson = People.FirstOrDefault(person => person.Id == createdPersonId);
        }
        finally { _suppressPersonSelectionLoad = false; }
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
            var path = await GetFaceThumbnailAsync(item.Record);
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
    SuggestedGroup,
    Unassigned,
    Ignored
}

public sealed partial class SuggestedPersonGroupViewModel(SuggestedPersonGroupRecord record) : ObservableObject
{
    public SuggestedPersonGroupRecord Record { get; } = record;
    private readonly List<UnassignedFaceRecord> _faces = record.Faces.ToList();
    public IReadOnlyList<UnassignedFaceRecord> Faces => _faces;
    public int FaceCount => _faces.Count;
    public string Summary => $"{Record.PersonName} · {FaceCount} 张高相似待确认";

    public void RemoveFaces(IReadOnlySet<long> faceIds)
    {
        if (_faces.RemoveAll(face => faceIds.Contains(face.FaceId)) == 0) return;
        OnPropertyChanged(nameof(Faces));
        OnPropertyChanged(nameof(FaceCount));
        OnPropertyChanged(nameof(Summary));
    }
}

public partial class UnassignedFaceViewModel(UnassignedFaceRecord record, string? initialThumbnailPath = null) : ObservableObject
{
    private IReadOnlyList<UnassignedFaceViewModel>? _burstOperationFaces;
    private Action<UnassignedFaceViewModel>? _toggleBurst;
    public UnassignedFaceRecord Record { get; } = record;
    public long FaceId => Record.FaceId;
    public string ImagePath => Record.ImagePath;
    public int Ordinal => Record.Ordinal + 1;
    public float Confidence => Record.Confidence;
    public string SuggestedText => Record.AssignmentState switch
    {
        "confirmed" when Record.SuggestedPersonName is not null => $"已归类：{Record.SuggestedPersonName}",
        "suggested" when Record.SuggestedPersonName is not null => $"可能是：{Record.SuggestedPersonName}",
        _ => "未归类"
    };
    public IReadOnlyList<UnassignedFaceViewModel> BurstOperationFaces => _burstOperationFaces ?? [this];
    public bool IsBurstStack => BurstOperationFaces.Count > 1;
    public int BurstCount => BurstOperationFaces.Select(face => face.Record.ImageId).Distinct().Count();
    public string BurstBadge => IsBurstStack ? $"连拍 · {BurstCount} 张" : "";
    public string BurstToggleText => IsBurstExpanded ? "−" : "+";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BurstToggleText))]
    private bool _isBurstExpanded;

    public void ConfigureBurst(IReadOnlyList<UnassignedFaceViewModel> members, Action<UnassignedFaceViewModel> toggle)
    {
        _burstOperationFaces = members;
        _toggleBurst = toggle;
        OnPropertyChanged(nameof(BurstOperationFaces));
        OnPropertyChanged(nameof(IsBurstStack));
        OnPropertyChanged(nameof(BurstCount));
        OnPropertyChanged(nameof(BurstBadge));
    }

    [RelayCommand]
    private void ToggleBurst() => _toggleBurst?.Invoke(this);

    [ObservableProperty]
    private string? _faceThumbnailPath = initialThumbnailPath;

    [ObservableProperty]
    private bool _isAutomaticSuggestion = string.Equals(record.AssignmentState, "suggested", StringComparison.Ordinal);
}

public partial class CandidateClusterViewModel(FaceCandidateClusterRecord record) : ObservableObject
{
    public FaceCandidateClusterRecord Record { get; } = record;
    private readonly List<UnassignedFaceRecord> _faces = record.Faces.ToList();
    public IReadOnlyList<UnassignedFaceRecord> Faces => _faces;
    public int FaceCount => _faces.Count;
    public string Summary => $"候选人物 · {FaceCount} 张脸";

    public void RemoveFaces(IReadOnlySet<long> faceIds)
    {
        if (_faces.RemoveAll(face => faceIds.Contains(face.FaceId)) == 0) return;
        OnPropertyChanged(nameof(Faces));
        OnPropertyChanged(nameof(FaceCount));
        OnPropertyChanged(nameof(Summary));
    }

    [ObservableProperty]
    private string? _representativeThumbnailPath;
}
