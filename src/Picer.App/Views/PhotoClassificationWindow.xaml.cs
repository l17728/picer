using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using Picer.App.Infrastructure;
using Picer.Core.Vision;

namespace Picer.App.Views;

public partial class PhotoClassificationWindow : Window
{
    private readonly HttpClient? _remoteClient;
    private CancellationTokenSource? _remoteRunCancellation;
    private string? _remoteRunId;

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
        };
        Loaded += async (_, _) =>
        {
            if (AppServices.ServiceMode)
            {
                StatusText.Text = "远程分类由照片空间服务执行，关闭窗口不会删除已完成结果。";
                var latest = await _remoteClient!.GetFromJsonAsync<ClassificationRunProgress>("latest");
                if (latest is not null) { _remoteRunId = latest.RunId; ShowProgress(latest); }
            }
            else await ClassificationJobCoordinator.RefreshAsync();
            await LoadClusterSettingsAsync();
            await LoadItemsAsync();
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
    private async void PreviewThreshold_Click(object sender, RoutedEventArgs e)
    {
        var threshold = (float)ClusterThreshold.Value;
        var preview = AppServices.ServiceMode
            ? await _remoteClient!.GetFromJsonAsync<ContentClusterPreview>($"clustering/preview?threshold={threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
            : await UiWork.RunDatabaseAsync("classification-cluster-preview", () => AppServices.ContentSemanticClusters.Preview(threshold));
        if (preview is not null) ThresholdHelp.Text = $"预览：约 {preview.EstimatedClusters} 个动态分类，涉及 {preview.EstimatedClusteredImages} 张照片。";
    }
    private async void ApplyThreshold_Click(object sender, RoutedEventArgs e)
    {
        var automatic = AutomaticThreshold.IsChecked == true; var threshold = (float)ClusterThreshold.Value;
        if (AppServices.ServiceMode)
        {
            using var response = await _remoteClient!.PostAsJsonAsync("clustering/settings", new { Automatic = automatic, ManualThreshold = threshold });
            response.EnsureSuccessStatusCode();
        }
        else await UiWork.RunDatabaseAsync("classification-cluster-settings", () =>
        {
            var settings = AppServices.ContentSemanticClusters.SaveSettings(automatic, threshold);
            AppServices.ContentSemanticClusters.RebuildLatest(settings.EffectiveThreshold);
        });
        await LoadClusterSettingsAsync(); await LoadItemsAsync();
        FeedbackText.Text = "动态分类阈值已应用；标签未变化的照片保留原分类。";
    }

    private async Task LoadClusterSettingsAsync()
    {
        var settings = AppServices.ServiceMode
            ? await _remoteClient!.GetFromJsonAsync<ContentClusterSettings>("clustering/settings")
            : await UiWork.RunDatabaseAsync("classification-cluster-settings-load", AppServices.ContentSemanticClusters.GetSettings);
        if (settings is null) return;
        AutomaticThreshold.IsChecked = settings.Automatic;
        ClusterThreshold.Value = settings.ManualThreshold;
        ClusterThreshold.IsEnabled = !settings.Automatic;
        ThresholdHelp.Tag = settings.RecommendedThreshold;
        UpdateThresholdHelp();
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
        var view = tab.Tag?.ToString() ?? "pending";
        ReviewRow[] rows;
        if (AppServices.ServiceMode)
        {
            var items = await _remoteClient!.GetFromJsonAsync<ClassificationReviewItem[]>(
                $"items?view={Uri.EscapeDataString(view)}&limit=500") ?? [];
            rows = items.Select(item => new ReviewRow(item.ImageId, item.Path, item.ThumbnailPath,
                item.ConceptId, item.DisplayName, SourceText(item.SourceKind, item.AssertionState,
                    item.ReviewState), item.ReferenceCount)).ToArray();
        }
        else rows = await UiWork.RunDatabaseAsync("classification-list", () =>
        {
            AppServices.ContentSemanticClusters.RefreshLabelsIncremental();
            if (view == "unclassified")
                return AppServices.ClassificationProjection.ListUnclassified().Select(image =>
                    new ReviewRow(image.Id, image.Path, image.ThumbPath, "—", "无分类", "处理完成，无有效分类", 1)).ToArray();
            return AppServices.ClassificationProjection.List(view).Select(item => new ReviewRow(item.ImageId,
                item.Path, item.ThumbnailPath, item.ConceptId, item.DisplayName,
                SourceText(item.SourceKind, item.AssertionState, item.ReviewState), item.ReferenceCount)).ToArray();
        });
        Items.ItemsSource = rows;
        FeedbackText.Text = $"当前页签：分类项 {rows.Length} 条，涉及照片 {rows.Select(row => row.ImageId).Distinct().Count()} 张。";
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
        (_, _, "rejected") => "已忽略（机器建议）",
        (_, "suggested", _) => "建议/待确认",
        _ => "自动归类（机器结果）"
    };

    private sealed record ReviewRow(long ImageId, string Path, string? ThumbnailPath, string ConceptId,
        string DisplayName, string SourceText, int ReferenceCount)
    {
        public string PreviewPath => File.Exists(ThumbnailPath) ? ThumbnailPath! : Path;
        public string ReferenceText => $"路径引用 {ReferenceCount} 个";
    }
}
