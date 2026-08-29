using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Picer.Core.Vision;

namespace Picer.App.Views;

public partial class FlorenceValidationWindow : Window
{
    private readonly FlorenceRuntimeValidationService _service; private readonly string _settingsPath;
    private CancellationTokenSource? _cancellation; private FlorenceValidationResult? _cpu; private FlorenceValidationResult? _directMl;
    private CancellationTokenSource? _inferenceCancellation; private string? _imagePath;
    public FlorenceValidationWindow(FlorenceRuntimeValidationService service, string settingsPath) { InitializeComponent(); _service = service; _settingsPath = settingsPath; StatusText.Text = $"模型目录：{service.ModelDirectory}"; }
    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        _cancellation = new(); RunButton.IsEnabled = false; CancelButton.IsEnabled = true; Progress.IsIndeterminate = true; var rows = new List<ResultRow>();
        try
        {
            var report = new Progress<string>(x => StatusText.Text = x);
            _cpu = await _service.ValidateAsync(FlorenceExecutionProvider.Cpu, report, _cancellation.Token); AddRows(rows, _cpu);
            _directMl = await _service.ValidateAsync(FlorenceExecutionProvider.DirectML, report, _cancellation.Token); AddRows(rows, _directMl);
            Results.ItemsSource = rows;
            var selected = Recommend(); SelectedText.Text = $"推荐：{selected}";
            StatusText.Text = selected == FlorenceExecutionProvider.DirectML ? "DirectML 四子模型全部兼容且热态更快，推荐 GPU。" : "默认 CPU：DirectML 不兼容或没有形成明确性能优势。";
        }
        catch (OperationCanceledException) { StatusText.Text = "验证已取消。"; } catch (Exception ex) { StatusText.Text = $"验证失败：{ex.Message}"; }
        finally { Progress.IsIndeterminate = false; RunButton.IsEnabled = true; CancelButton.IsEnabled = false; _cancellation.Dispose(); _cancellation = null; }
    }
    private static void AddRows(List<ResultRow> rows, FlorenceValidationResult result) { foreach (var stage in result.Stages) rows.Add(new(result.Provider.ToString(), stage.Stage, stage.Loaded, stage.LoadMilliseconds.ToString("F0"), stage.Stage == "视觉编码器" ? result.VisionColdMilliseconds.ToString("F0") : "-", stage.Stage == "视觉编码器" ? result.VisionWarmMilliseconds.ToString("F0") : "-", stage.Detail)); }
    private FlorenceExecutionProvider Recommend() => _directMl is { Available: true } dml && _cpu is { Available: true } cpu && dml.VisionWarmMilliseconds < cpu.VisionWarmMilliseconds * .9 ? FlorenceExecutionProvider.DirectML : FlorenceExecutionProvider.Cpu;
    private void Cancel_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_cpu is null || _directMl is null) { StatusText.Text = "请先完成一次 CPU + DirectML 验证。"; return; }
        var selected = Recommend(); Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new { provider = selected.ToString(), model = "Florence-2-base-ft-int8-e88a44e", validatedUtc = DateTime.UtcNow, cpu = _cpu, directML = _directMl }, new JsonSerializerOptions { WriteIndented = true }));
        StatusText.Text = $"已保存推荐选择：{selected}。后续本地描述任务将只使用通过验证的提供程序。";
    }
    private void ChooseImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择要本地分析的图片", Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.gif|所有文件|*.*" };
        if (dialog.ShowDialog(this) != true) return; _imagePath = dialog.FileName; ImagePathText.Text = _imagePath;
        try { Preview.Source = new BitmapImage(new Uri(_imagePath)); } catch { Preview.Source = null; }
    }
    private async void Infer_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_imagePath)) { StatusText.Text = "请先选择图片。"; return; }
        var provider = _cpu is not null && _directMl is not null ? Recommend() : LoadSavedProvider();
        _inferenceCancellation = new(); InferButton.IsEnabled = false; InferCancelButton.IsEnabled = true; Progress.IsIndeterminate = true; InferenceText.Text = "";
        try
        {
            using var service = new FlorenceInferenceService(_service.ModelDirectory, provider);
            var task = TaskBox.SelectedIndex switch { 1 => FlorenceTask.DetailedCaption, 2 => FlorenceTask.Ocr, _ => FlorenceTask.Caption };
            var result = await service.RunAsync(_imagePath, task, task == FlorenceTask.DetailedCaption ? 128 : 96, new Progress<string>(x => StatusText.Text = x), _inferenceCancellation.Token);
            InferenceText.Text = result.Text; StatusText.Text = $"完成：{result.Provider}，生成 {result.GeneratedTokens} 个词元，用时 {result.Elapsed.TotalSeconds:F1} 秒。";
        }
        catch (OperationCanceledException) { StatusText.Text = "本地分析已取消。"; } catch (Exception ex) { StatusText.Text = $"本地分析失败：{ex.Message}"; }
        finally { Progress.IsIndeterminate = false; InferButton.IsEnabled = true; InferCancelButton.IsEnabled = false; _inferenceCancellation.Dispose(); _inferenceCancellation = null; }
    }
    private FlorenceExecutionProvider LoadSavedProvider()
    {
        try { using var document = JsonDocument.Parse(File.ReadAllText(_settingsPath)); if (document.RootElement.TryGetProperty("provider", out var value) && Enum.TryParse<FlorenceExecutionProvider>(value.GetString(), out var provider)) return provider; } catch { }
        return FlorenceExecutionProvider.Cpu;
    }
    private void InferCancel_Click(object sender, RoutedEventArgs e) => _inferenceCancellation?.Cancel();
    private async void SaveEvaluation_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_imagePath) || string.IsNullOrWhiteSpace(InferenceText.Text)) { StatusText.Text = "请先完成一次图片分析。"; return; }
        try
        {
            await using var stream = File.OpenRead(_imagePath); var fingerprint = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            var path = Path.Combine(AppPaths.DataRoot, "florence-quality.jsonl"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var record = new { imageSha256 = fingerprint, task = TaskBox.SelectedIndex, output = InferenceText.Text, expected = ExpectedText.Text, quality = QualityBox.SelectedIndex, provider = LoadSavedProvider().ToString(), createdUtc = DateTime.UtcNow };
            await File.AppendAllTextAsync(path, JsonSerializer.Serialize(record) + Environment.NewLine); StatusText.Text = "质量标注已保存；仅记录图片哈希，不记录原图路径。";
        }
        catch (Exception ex) { StatusText.Text = $"保存质量标注失败：{ex.Message}"; }
    }
    private sealed record ResultRow(string Provider, string Stage, bool Loaded, string LoadText, string ColdText, string WarmText, string Detail);
}
