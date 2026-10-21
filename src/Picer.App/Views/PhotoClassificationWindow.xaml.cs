using System.IO;
using System.Windows;
using System.Windows.Controls;
using Picer.App.Infrastructure;
using Picer.Core.Vision;

namespace Picer.App.Views;

public partial class PhotoClassificationWindow : Window
{
    public PhotoClassificationWindow()
    {
        InitializeComponent();
        CorrectionConcept.ItemsSource = VisualConceptCatalog.DisplayNames.OrderBy(item => item.Value).ToArray();
        CorrectionConcept.SelectedValuePath = "Key";
        CorrectionConcept.DisplayMemberPath = "Value";
        ClassificationJobCoordinator.ProgressChanged += OnProgressChanged;
        Closed += (_, _) => ClassificationJobCoordinator.ProgressChanged -= OnProgressChanged;
        Loaded += async (_, _) =>
        {
            if (AppServices.ServiceMode)
            {
                StatusText.Text = "远程模式当前只读；照片分类与审核首版仅支持本地索引。";
                StartButton.IsEnabled = false;
            }
            await ClassificationJobCoordinator.RefreshAsync();
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

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (AppServices.ServiceMode) return;
        ClassificationJobCoordinator.StartOrResume();
    }

    private async void Pause_Click(object sender, RoutedEventArgs e) => await ClassificationJobCoordinator.PauseAsync();
    private async void Cancel_Click(object sender, RoutedEventArgs e) => await ClassificationJobCoordinator.CancelAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadItemsAsync();
    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await LoadItemsAsync();
    }

    private async Task LoadItemsAsync()
    {
        if (Tabs.SelectedItem is not TabItem tab) return;
        var view = tab.Tag?.ToString() ?? "pending";
        var rows = await UiWork.RunDatabaseAsync("classification-list", () =>
        {
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
        await ApplySelectedAsync("确认", (row) => AppServices.VisualConceptRepository.Confirm(row.ImageId, row.ConceptId), sameConcept: true);
    private async void Reject_Click(object sender, RoutedEventArgs e) =>
        await ApplySelectedAsync("拒绝", (row) => AppServices.VisualConceptRepository.Reject(row.ImageId, row.ConceptId), sameConcept: true);
    private async void RemoveMachine_Click(object sender, RoutedEventArgs e) =>
        await ApplySelectedAsync("移除机器分类", (row) => AppServices.VisualConceptRepository.RemoveMachineClassification(row.ImageId, row.ConceptId));
    private async void Restore_Click(object sender, RoutedEventArgs e) =>
        await ApplySelectedAsync("恢复", (row) => AppServices.ClassificationProjection.RestoreSuggestion(row.ImageId, row.ConceptId), sameConcept: true);
    private async void Correct_Click(object sender, RoutedEventArgs e)
    {
        if (CorrectionConcept.SelectedValue is not string conceptId)
        {
            FeedbackText.Text = "请先在“改为”右侧选择目标类别。";
            return;
        }
        await ApplySelectedAsync("更正", row => AppServices.VisualConceptRepository.Correct(row.ImageId,
            row.ConceptId, conceptId));
    }

    private async Task ApplySelectedAsync(string operation, Action<ReviewRow> action, bool sameConcept = false)
    {
        var selected = Items.SelectedItems.Cast<ReviewRow>().Where(row => row.ConceptId != "—").ToArray();
        if (selected.Length == 0) { FeedbackText.Text = "请先选择分类项。"; return; }
        if (sameConcept && selected.Select(row => row.ConceptId).Distinct(StringComparer.Ordinal).Count() != 1)
        { FeedbackText.Text = "批量操作只能选择同一个类别。"; return; }
        await UiWork.RunDatabaseAsync("classification-review", () => { foreach (var row in selected) action(row); });
        FeedbackText.Text = $"已{operation} {selected.Length} 条分类项。";
        await LoadItemsAsync();
    }

    private static string SourceText(string source, string state, string review) => (source, state, review) switch
    {
        ("metadata_rule", _, _) => "自动归类（可核验元数据）",
        ("user", _, _) => "用户已确认",
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
