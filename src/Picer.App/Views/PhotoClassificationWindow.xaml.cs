using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Picer.App.Infrastructure;
using Picer.Core.Models;
using Picer.Core.Vision;

namespace Picer.App.Views;

public partial class PhotoClassificationWindow : Window
{
    private readonly HttpClient? _remoteClient;
    private CancellationTokenSource? _remoteRunCancellation;
    private string? _remoteRunId;
    private ContentClusterSettings? _loadedClusterSettings;
    private int _itemsGeneration;
    private bool _refreshingLabels;
    private bool _refreshingRecommendation;
    private readonly CancellationTokenSource _previewCancellation = new();

    public PhotoClassificationWindow()
    {
        InitializeComponent();
        if (AppServices.ServiceMode) _remoteClient = AppServices.CreateClassificationHttpClient();
        CorrectionConcept.ItemsSource = VisualConceptCatalog.DisplayNames.OrderBy(item => item.Value).ToArray();
        CorrectionConcept.SelectedValuePath = "Key";
        CorrectionConcept.DisplayMemberPath = "Value";
        ClassificationJobCoordinator.ProgressChanged += OnProgressChanged;
        Closed += (_, _) =>
        {
            ClassificationJobCoordinator.ProgressChanged -= OnProgressChanged;
            _remoteRunCancellation?.Cancel();
            _remoteClient?.Dispose();
            _previewCancellation.Cancel();
        };
        Loaded += async (_, _) =>
        {
            try
            {
            await Task.WhenAll(LoadItemsAsync(), LoadClusterSettingsAsync());
            if (AppServices.ServiceMode)
            {
                StatusText.Text = "远程分类由照片空间服务执行，关闭窗口不会删除已完成结果。";
                var latest = await _remoteClient!.GetFromJsonAsync<ClassificationRunProgress>("latest");
                if (latest is not null) { _remoteRunId = latest.RunId; ShowProgress(latest); }
            }
            else await ClassificationJobCoordinator.RefreshAsync();
            var repaired = 0;
            string? repairError = null;
            try
            {
                repaired = await RepairObsoleteScreenshotClassificationsAsync();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception exception)
            {
                repairError = exception.Message;
            }
            await LoadClusterSettingsAsync();
            await LoadItemsAsync();
            if (repairError is not null) FeedbackText.Text = "旧截图分类修复未完成：" + repairError;
            else if (repaired > 0) FeedbackText.Text = $"已修复 {repaired} 张照片的旧“屏幕截图”分类，并根据已有描述和元数据重新整理标签。";
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { if (!_previewCancellation.IsCancellationRequested) FeedbackText.Text = "加载失败：" + exception.Message; }
        };
    }

    private void OnProgressChanged(object? sender, ClassificationRunProgress progress) =>
        Dispatcher.InvokeAsync(async () => { ShowProgress(progress); await LoadItemsAsync(); });

    private void ShowProgress(ClassificationRunProgress value)
    {
        Progress.Value = value.Total == 0 ? 0 : value.Processed * 100d / value.Total;
        StatusText.Text = value.Status switch
        {
            "running" => $"正在整理：已处理 {value.Processed} / {value.Total}；可以关闭窗口继续使用 Picer。",
            "paused" => $"任务已暂停；已完成 {value.Processed} 项，结果已保存。",
            "cancelled" => $"本次任务已取消；已提交 {value.Processed} 项仍保留，可稍后继续。",
            "interrupted" => $"上次任务已中断；已完成 {value.Processed} / {value.Total}，请点击“继续未完成分类”。",
            "partial_failed" => $"整理完成，{value.Failed} 项失败；其他结果已保存。",
            "completed" => $"整理完成：已处理 {value.Processed} 张照片。",
            _ => "还没有自动分类结果。"
        };
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (AppServices.ServiceMode)
        {
            if (_remoteRunCancellation is not null) return;
            _remoteRunCancellation = new CancellationTokenSource();
            try
            {
                var progress = await PostAsync<ClassificationRunProgress>("begin", null, _remoteRunCancellation.Token);
                _remoteRunId = progress.RunId;
                while (progress.Status == "running")
                {
                    ShowProgress(progress);
                    progress = await PostAsync<ClassificationRunProgress>($"process/{progress.RunId}?batchSize=20",
                        null, _remoteRunCancellation.Token);
                    await LoadItemsAsync();
                }
                ShowProgress(progress);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { StatusText.Text = "远程分类失败：" + exception.Message; }
            finally { _remoteRunCancellation.Dispose(); _remoteRunCancellation = null; }
            return;
        }
        ClassificationJobCoordinator.StartOrResume();
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (AppServices.ServiceMode) { await StopRemoteAsync("pause"); return; }
        await ClassificationJobCoordinator.PauseAsync();
    }
    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (AppServices.ServiceMode) { await StopRemoteAsync("cancel"); return; }
        await ClassificationJobCoordinator.CancelAsync();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadItemsAsync();
    private void ThresholdMode_Changed(object sender, RoutedEventArgs e)
    {
        if (ClusterThreshold is not null) ClusterThreshold.IsEnabled = AutomaticThreshold.IsChecked != true;
        UpdateThresholdHelp();
    }
    private void Threshold_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateThresholdHelp();
    private long _previewGeneration;
    private async void PreviewThreshold_Click(object sender, RoutedEventArgs e)
    {
        var generation = ++_previewGeneration;
        try
        {
        var threshold = (float)ClusterThreshold.Value;
        var preview = AppServices.ServiceMode
            ? await _remoteClient!.GetFromJsonAsync<ContentClusterPreview>($"clustering/preview?threshold={threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
            : await PreviewLocalAsync(threshold);
        if (preview is not null && generation == _previewGeneration && !_previewCancellation.IsCancellationRequested)
            ThresholdHelp.Text = $"预览：约 {preview.EstimatedClusters} 个动态分类，涉及 {preview.EstimatedClusteredImages} 张照片。";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_previewCancellation.IsCancellationRequested) FeedbackText.Text = "预览失败：" + exception.Message; }
    }
    private async void ApplyThreshold_Click(object sender, RoutedEventArgs e)
    {
        var baseline=_loadedClusterSettings;
        if (baseline is null) { FeedbackText.Text="分类设置尚未加载，请稍后重试。"; return; }
        if (sender is Button apply) apply.IsEnabled=false;
        try
        {
        var automatic = AutomaticThreshold.IsChecked == true; var threshold = (float)ClusterThreshold.Value;
        if (AppServices.ServiceMode)
        {
            using var response = await _remoteClient!.PostAsJsonAsync("clustering/settings", new { Automatic = automatic, ManualThreshold = threshold, ExpectedVersion=baseline.Version,ExpectedEpoch=baseline.Epoch,ModelVersion=baseline.ModelVersion });
            if (response.StatusCode==System.Net.HttpStatusCode.Conflict)
            {
                using var error=System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var errorCode=error.RootElement.TryGetProperty("error",out var code) ? code.GetString() : null;
                var retained=errorCode=="metadata_conflict_pending";
                await LoadClusterSettingsAsync();
                FeedbackText.Text=errorCode=="cluster_model_changed" ? "分类模型已更新，已重新加载设置；请确认后重新应用。"
                    : retained ? "另一台电脑已修改分类设置。你的修改已保留，请从“索引巡检、同步与修复 → 处理同步冲突…”选择要采用的版本。"
                    : "照片空间已重置，已重新加载分类设置；请确认后重新应用。";
                return;
            }
            response.EnsureSuccessStatusCode();
        }
        else
        {
            var settings = await UiWork.RunDatabaseAsync("classification-cluster-settings", () =>
                AppServices.ContentSemanticClusters.SaveSettings(automatic, threshold,
                    expectedVersion:baseline.Version, expectedOperationId:baseline.OperationId,
                    expectedModelVersion:baseline.ModelVersion, refreshRecommendation:false));
            var compute = await UiWork.ReadSnapshotAsync("Classification.RebuildInput", AppServices.Db,
                reader => new ContentSemanticClusterService(reader).CaptureRebuild(settings), _previewCancellation.Token);
            var plan = await Task.Run(compute, _previewCancellation.Token);
            await UiWork.RunDatabaseAsync("Classification.RebuildCommit", () => plan.Commit(AppServices.Db), _previewCancellation.Token);
        }
        await LoadClusterSettingsAsync(); await LoadItemsAsync();
        if (!AppServices.ServiceMode) AppServices.QueueImmediateRemoteSync("动态分类阈值更新");
        FeedbackText.Text = "动态分类阈值已应用；标签未变化的照片保留原分类。";
        }
        catch (InvalidOperationException exception) when (exception.Message=="metadata_conflict_pending")
        { FeedbackText.Text="分类设置已被更新。你的修改已保留，请从“索引巡检、同步与修复 → 处理同步冲突…”选择版本。"; }
        catch (InvalidOperationException exception) when (exception.Message=="cluster_model_changed")
        { await LoadClusterSettingsAsync(); FeedbackText.Text="分类模型已更新，已重新加载设置；请确认后重新应用。"; }
        catch (InvalidOperationException exception) when (exception.Message=="cluster_input_changed")
        { await LoadClusterSettingsAsync(); await LoadItemsAsync(); FeedbackText.Text="设置已保存，但图库在计算期间发生变化；请重新应用以更新分类。"; }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or IOException)
        { FeedbackText.Text="分类设置未保存，请检查连接后重试。"; }
        finally { if (sender is Button button) button.IsEnabled=true; }
    }

    private async Task LoadClusterSettingsAsync()
    {
        var settings = AppServices.ServiceMode
            ? await _remoteClient!.GetFromJsonAsync<ContentClusterSettings>("clustering/settings")
            : await UiWork.ReadSnapshotAsync("Classification.Settings", AppServices.Db,
                reader => new ContentSemanticClusterService(reader).ReadSettings(), _previewCancellation.Token);
        if (settings is null) return;
        _loadedClusterSettings=settings;
        AutomaticThreshold.IsChecked = settings.Automatic;
        ClusterThreshold.Value = settings.ManualThreshold;
        ClusterThreshold.IsEnabled = !settings.Automatic;
        ThresholdHelp.Tag = settings.RecommendedThreshold;
        UpdateThresholdHelp();
        if (!AppServices.ServiceMode && !_refreshingRecommendation) _ = RefreshRecommendationAsync();
    }

    private void UpdateThresholdHelp()
    {
        if (ThresholdHelp is null || ClusterThreshold is null) return;
        var recommended = ThresholdHelp.Tag is float value ? value : 0.82f;
        ThresholdHelp.Text = AutomaticThreshold?.IsChecked == true
            ? $"自动模式；当前图库推荐值 {recommended:F2}。"
            : $"人工值 {ClusterThreshold.Value:F2}；推荐值 {recommended:F2}。数值越高，动态分类越小且更相似。";
    }
    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await LoadItemsAsync();
    }

    private async Task LoadItemsAsync()
    {
        if (Tabs.SelectedItem is not TabItem tab) return;
        var generation = Interlocked.Increment(ref _itemsGeneration);
        var view = tab.Tag?.ToString() ?? "pending";
        OwnershipHelp.Visibility = view == "ownership" ? Visibility.Visible : Visibility.Collapsed;
        ReviewRow[] rows;
        if (AppServices.ServiceMode)
        {
            var items = await _remoteClient!.GetFromJsonAsync<ClassificationReviewItem[]>(
                $"items?view={Uri.EscapeDataString(view)}&limit=500") ?? [];
            rows = items.Select(item => new ReviewRow(item.ImageId, item.Path, item.ThumbnailPath,
                item.ConceptId, item.DisplayName, SourceText(item.SourceKind, item.AssertionState,
                    item.ReviewState), item.ReferenceCount)).ToArray();
        }
        else rows = await UiWork.ReadSnapshotAsync("Classification.List", AppServices.Db, reader =>
        {
            var projection = new ClassificationProjectionService(reader);
            if (view == "unclassified")
                return projection.ListUnclassified().Select(image =>
                    new ReviewRow(image.Id, image.Path, image.ThumbPath, "—", "无分类", "处理完成，无有效分类", 1)).ToArray();
            return projection.List(view).Select(item => new ReviewRow(item.ImageId,
                item.Path, item.ThumbnailPath, item.ConceptId, item.DisplayName,
                SourceText(item.SourceKind, item.AssertionState, item.ReviewState), item.ReferenceCount)).ToArray();
        }, _previewCancellation.Token);
        if (generation != Volatile.Read(ref _itemsGeneration) || _previewCancellation.IsCancellationRequested) return;
        Items.ItemsSource = rows;
        if (!AppServices.ServiceMode && !_refreshingLabels) _ = RefreshLabelsAsync();
        FeedbackText.Text = $"当前页签：分类项 {rows.Length} 条，涉及照片 {rows.Select(row => row.ImageId).Distinct().Count()} 张。";
    }

    private async Task<ContentClusterPreview> PreviewLocalAsync(float threshold)
    {
        var compute = await UiWork.ReadSnapshotAsync("Classification.PreviewInput", AppServices.Db,
            reader => new ContentSemanticClusterService(reader).CapturePreview(threshold), _previewCancellation.Token);
        return await Task.Run(compute, _previewCancellation.Token);
    }

    private async Task RefreshLabelsAsync()
    {
        _refreshingLabels = true;
        try
        {
            var changed = await UiWork.RunDatabaseAsync("Classification.RefreshLabels",
                AppServices.ContentSemanticClusters.RefreshLabelsIncremental, _previewCancellation.Token);
            if (changed > 0 && !_previewCancellation.IsCancellationRequested) await LoadItemsAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Picer.Core.Logging.AppLogger.Warn($"Classification labels refresh failed: {exception.GetType().Name}"); }
        finally { _refreshingLabels = false; }
    }

    private async Task RefreshRecommendationAsync()
    {
        _refreshingRecommendation = true;
        try
        {
            var input = await UiWork.ReadSnapshotAsync("Classification.RecommendationInput", AppServices.Db,
                reader => new ContentSemanticClusterService(reader).CaptureRecommendation(), _previewCancellation.Token);
            if (!input.NeedsRefresh) return;
            var recommendation = await Task.Run(input.Calculate, _previewCancellation.Token);
            if (!await UiWork.RunDatabaseAsync("Classification.RecommendationCommit",
                () => AppServices.ContentSemanticClusters.SaveRecommendation(input, recommendation), _previewCancellation.Token)) return;
            if (_previewCancellation.IsCancellationRequested || _loadedClusterSettings?.ModelVersion != input.Model) return;
            ThresholdHelp.Tag = recommendation;
            UpdateThresholdHelp();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Picer.Core.Logging.AppLogger.Warn($"Classification recommendation failed: {exception.GetType().Name}"); }
        finally { _refreshingRecommendation = false; }
    }

    private async Task<int> RepairObsoleteScreenshotClassificationsAsync()
    {
        if (AppServices.ServiceMode)
        {
            using var response = await _remoteClient!.PostAsync("repair-screenshots", null, _previewCancellation.Token);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<ScreenshotRepairResult>(cancellationToken: _previewCancellation.Token))?.RepairedImages ?? 0;
        }
        return await UiWork.RunDatabaseAsync("classification-repair-screenshots",
            () => AppServices.ClassificationProjection.RepairObsoleteScreenshotClassifications(_previewCancellation.Token),
            _previewCancellation.Token);
    }

    private async void Preview_Loaded(object sender, RoutedEventArgs e)
    {
        if (!AppServices.ServiceMode || sender is not Image image || image.DataContext is not ReviewRow row ||
            row.PreviewPath.Length > 0 || row.PreviewLoading) return;
        row.PreviewLoading = true;
        try
        {
            var record = new ImageRecord { Id = row.ImageId };
            var path = AppServices.RemoteThumbnails.TryGet(record) ?? await AppServices.RemoteThumbnails.GetOrDownloadAsync(
                record, token => AppServices.Gateway.OpenThumbnailAsync(row.ImageId, token), _previewCancellation.Token);
            if (path is not null && File.Exists(path))
            {
                row.PreviewPath = path;
                image.Source = LoadPreview(path);
            }
        }
        catch (OperationCanceledException) { }
        catch (HttpRequestException) { }
        catch (IOException) { }
        finally { row.PreviewLoading = false; }
    }

    private static BitmapImage LoadPreview(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 256;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e) =>
        await ApplySelectedAsync("确认", "confirm", row => AppServices.VisualConceptRepository.Confirm(row.ImageId, row.ConceptId), sameConcept: true);
    private async void Reject_Click(object sender, RoutedEventArgs e) =>
        await ApplySelectedAsync("拒绝", "reject", row => AppServices.VisualConceptRepository.Reject(row.ImageId, row.ConceptId), sameConcept: true);
    private async void RemoveMachine_Click(object sender, RoutedEventArgs e) =>
        await ApplySelectedAsync("移除机器分类", "remove", row => AppServices.VisualConceptRepository.RemoveMachineClassification(row.ImageId, row.ConceptId));
    private async void Restore_Click(object sender, RoutedEventArgs e) =>
        await ApplySelectedAsync("恢复", "restore", row => AppServices.ClassificationProjection.RestoreSuggestion(row.ImageId, row.ConceptId), sameConcept: true);
    private async void Correct_Click(object sender, RoutedEventArgs e)
    {
        if (CorrectionConcept.SelectedValue is not string conceptId)
        {
            FeedbackText.Text = "请先在“改为”右侧选择目标类别。";
            return;
        }
        await ApplySelectedAsync("更正", "correct", row => AppServices.VisualConceptRepository.Correct(row.ImageId,
            row.ConceptId, conceptId), newConceptId: conceptId);
    }

    private async Task ApplySelectedAsync(string operation, string remoteOperation, Action<ReviewRow> action,
        bool sameConcept = false, string? newConceptId = null)
    {
        var selected = Items.SelectedItems.Cast<ReviewRow>().Where(row => row.ConceptId != "—").ToArray();
        if (selected.Length == 0) { FeedbackText.Text = "请先选择分类项。"; return; }
        if (selected.Any(row => row.ConceptId.StartsWith("person.", StringComparison.Ordinal)))
        { FeedbackText.Text = "人物标签请在“人物”页面中修改。"; return; }
        if (sameConcept && selected.Select(row => row.ConceptId).Distinct(StringComparer.Ordinal).Count() != 1)
        { FeedbackText.Text = "批量操作只能选择同一个类别。"; return; }
        if (AppServices.ServiceMode)
        {
            foreach (var row in selected)
            {
                using var response = await _remoteClient!.PostAsJsonAsync(remoteOperation,
                    new { row.ImageId, row.ConceptId, NewConceptId = newConceptId });
                response.EnsureSuccessStatusCode();
            }
        }
        else await UiWork.RunDatabaseAsync("classification-review", () => { foreach (var row in selected) action(row); });
        if (!AppServices.ServiceMode) AppServices.QueueImmediateRemoteSync("分类审核提交");
        FeedbackText.Text = $"已{operation} {selected.Length} 条分类项。";
        await LoadItemsAsync();
    }

    private async Task StopRemoteAsync(string operation)
    {
        if (_remoteRunId is null) return;
        _remoteRunCancellation?.Cancel();
        using var response = await _remoteClient!.PostAsync($"{operation}/{_remoteRunId}", null);
        response.EnsureSuccessStatusCode();
        var latest = await _remoteClient.GetFromJsonAsync<ClassificationRunProgress>("latest");
        if (latest is not null) ShowProgress(latest);
    }

    private async Task<T> PostAsync<T>(string path, object? body, CancellationToken cancellationToken)
    {
        using var response = body is null
            ? await _remoteClient!.PostAsync(path, null, cancellationToken)
            : await _remoteClient!.PostAsJsonAsync(path, body, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("照片空间返回空的分类结果。");
    }

    private static string SourceText(string source, string state, string review) => (source, state, review) switch
    {
        ("metadata_rule", _, _) => "自动归类（可核验元数据）",
        ("user", _, _) => "用户已确认",
        ("person_metadata", _, _) => "照片人物标签（已确认）",
        ("legacy", _, _) => "旧版已生效（来源未记录）",
        ("ownership_review", _, _) => "历史拒绝（待核对来源）",
        (_, _, "rejected") => "已忽略（机器建议）",
        (_, "suggested", _) => "建议/待确认",
        _ => "自动归类（机器结果）"
    };

    private sealed class ReviewRow(long imageId, string path, string? thumbnailPath, string conceptId,
        string displayName, string sourceText, int referenceCount)
    {
        public long ImageId { get; } = imageId;
        public string Path { get; } = path;
        public string? ThumbnailPath { get; } = thumbnailPath;
        public string ConceptId { get; } = conceptId;
        public string DisplayName { get; } = displayName;
        public string SourceText { get; } = sourceText;
        public int ReferenceCount { get; } = referenceCount;
        public string PreviewPath { get; set; } = !string.IsNullOrWhiteSpace(thumbnailPath) && File.Exists(thumbnailPath)
            ? thumbnailPath
            : AppServices.ServiceMode ? "" : path;
        public bool PreviewLoading { get; set; }
        public string ReferenceText => $"路径引用 {ReferenceCount} 个";
    }

    private sealed record ScreenshotRepairResult(int RepairedImages);
}
