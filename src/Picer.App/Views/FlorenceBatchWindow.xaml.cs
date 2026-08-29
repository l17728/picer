using System.IO;
using System.Text.Json;
using System.Windows;
using Picer.Core.Vision;

namespace Picer.App.Views;

public partial class FlorenceBatchWindow : Window
{
    private readonly FlorenceResultRepository _repository; private readonly string _modelRoot, _settingsPath; private CancellationTokenSource? _cancellation; private List<ReviewRow> _rows = [];
    public FlorenceBatchWindow(FlorenceResultRepository repository, string modelRoot, string settingsPath) { InitializeComponent(); _repository = repository; _modelRoot = modelRoot; _settingsPath = settingsPath; RefreshRows(); }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(LimitBox.Text, out var limit) || limit is < 1 or > 500) { StatusText.Text = "本次处理数量必须为 1–500。"; return; }
        var task = TaskBox.SelectedIndex switch { 1 => FlorenceTask.DetailedCaption, 2 => FlorenceTask.Ocr, _ => FlorenceTask.Caption }; var provider = LoadProvider();
        var pending = _repository.GetPending(task.ToString(), "Florence-2-base-ft-int8-e88a44e").Take(limit).ToArray(); if (pending.Length == 0) { StatusText.Text = "没有需要处理的图片。"; return; }
        _cancellation = new(); StartButton.IsEnabled = false; CancelButton.IsEnabled = true; Progress.Maximum = pending.Length; Progress.Value = 0;
        try
        {
            StatusText.Text = $"正在加载 {provider} 模型……"; using var service = await System.Threading.Tasks.Task.Run(() => new FlorenceInferenceService(_modelRoot, provider), _cancellation.Token);
            for (var index = 0; index < pending.Length; index++)
            {
                _cancellation.Token.ThrowIfCancellationRequested(); var image = pending[index]; StatusText.Text = $"{index + 1}/{pending.Length}：{image.Path}";
                try { var result = await service.RunAsync(image.Path, task, task == FlorenceTask.DetailedCaption ? 128 : 96, cancellationToken: _cancellation.Token); _repository.SaveSuggested(image, task, provider, result.Text); }
                catch (Exception ex) when (ex is not OperationCanceledException) { StatusText.Text = $"跳过失败图片：{Path.GetFileName(image.Path)}；{ex.Message}"; }
                Progress.Value = index + 1;
            }
            StatusText.Text = "批量任务完成，请审阅结果后保存。"; RefreshRows();
        }
        catch (OperationCanceledException) { StatusText.Text = "任务已取消；已完成结果已保存，下次继续。"; RefreshRows(); }
        finally { StartButton.IsEnabled = true; CancelButton.IsEnabled = false; _cancellation.Dispose(); _cancellation = null; }
    }
    private FlorenceExecutionProvider LoadProvider() { try { using var document = JsonDocument.Parse(File.ReadAllText(_settingsPath)); if (document.RootElement.TryGetProperty("provider", out var value) && Enum.TryParse<FlorenceExecutionProvider>(value.GetString(), out var provider)) return provider; } catch { } return FlorenceExecutionProvider.Cpu; }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRows(); private void Cancel_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();
    private void RefreshRows() { _rows = _repository.GetForReview().Select(x => new ReviewRow(x.ImageId, x.Path, x.Task, x.Text, x.State, x.Provider) { Accepted = x.State == "accepted" }).ToList(); Rows.ItemsSource = _rows; StatusText.Text = $"已加载 {_rows.Count} 条本地结果。"; }
    private void SaveReview_Click(object sender, RoutedEventArgs e) { foreach (var row in _rows.Where(x => x.State == "suggested")) _repository.Review(row.ImageId, row.Task, row.Accepted); RefreshRows(); StatusText.Text = "审阅已保存；接受的文字现已进入主搜索。"; }
    private sealed class ReviewRow(long imageId, string path, string task, string text, string state, string provider) { public long ImageId { get; } = imageId; public string Path { get; } = path; public string Task { get; } = task; public string Text { get; } = text; public string State { get; } = state; public string Provider { get; } = provider; public bool Accepted { get; set; } }
}
