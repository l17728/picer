using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Picer.Core.Vision;
using Picer.App.ViewModels;

namespace Picer.App;

public partial class CloudVisionTaskCenterWindow : Window
{
    private readonly MainViewModel _mainViewModel;
    public CloudVisionTaskCenterWindow(MainViewModel mainViewModel) { _mainViewModel = mainViewModel; InitializeComponent(); LoadTasks(); }

    private void LoadTasks()
    {
        var tasks = AppServices.CloudVisionRepository.GetRecentTasks().Select(item => new TaskItem(item)).ToList();
        TasksGrid.ItemsSource = tasks;
        SummaryText.Text = $"共 {tasks.Count} 项 · 完成 {tasks.Count(x => x.Status == "completed")} · 失败 {tasks.Count(x => x.Status == "failed")} · 等待/处理中 {tasks.Count(x => x.Status is "queued" or "processing")}";
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadTasks();
    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        var records = TasksGrid.SelectedItems.Cast<TaskItem>().Where(item => item.Status == "failed")
            .Select(item => AppServices.Db.GetImage(item.ImageId)).Where(item => item is not null).Cast<Picer.Core.Models.ImageRecord>().ToList();
        if (records.Count == 0) { MessageBox.Show(this, "请选择至少一个失败任务。", "任务重试", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        await _mainViewModel.AnalyzeImagesWithCloudAsync(records); LoadTasks();
    }
    private void TasksGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TasksGrid.SelectedItem is not TaskItem item) return;
        if (!File.Exists(item.ImagePath))
        {
            MessageBox.Show(this, "原图片当前不可用，请在主窗口运行“检查缺失”并重新定位。", "无法定位图片",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { Process.Start("explorer.exe", $"/select,\"{item.ImagePath}\""); }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"无法在资源管理器中定位：{exception.Message}", "无法定位图片",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private sealed class TaskItem(CloudVisionRepository.TaskRecord record)
    {
        public string Status => record.Status;
        public long ImageId => record.ImageId;
        public string ImagePath => record.ImagePath;
        public string ModelVersion => record.ModelVersion;
        public string StatusLabel => record.Status switch { "queued" => "等待", "processing" => "处理中", "completed" => "完成", "failed" => "失败", _ => record.Status };
        public string ProviderLabel => record.Provider == "bailian" ? "百炼" : record.Provider == "zhipu" ? "智谱" : record.Provider;
        public string ReasonLabel => record.Reason switch { "user-requested" => "用户选择", "multiple-people" => "多人场景", "low-local-confidence" => "本地低置信", _ => record.Reason ?? "" };
        public string Detail => record.Summary ?? record.ErrorCode ?? "";
        public string TokenText => $"{record.InputTokens}/{record.OutputTokens}";
        public string CostText => $"¥{record.EstimatedCostYuan:0.0000}";
        public string UpdatedLocal => record.UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }
}
