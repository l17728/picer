using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Picer.Core.Models;
using Picer.Core.Vision;
using Picer.App.Infrastructure;

namespace Picer.App.Views;

public partial class FaceValidationWindow : Window
{
    private readonly FaceValidationService _validation;
    private readonly VisionAnalysisService? _analysis;
    private readonly string? _unavailableReason;
    private readonly ObservableCollection<YuNetValidationItem> _rows = [];
    private SFaceCalibrationResult? _lastCalibration;
    private int _operationRunning;

    public FaceValidationWindow(FaceValidationService validation, VisionAnalysisService? analysis, string? unavailableReason)
    {
        InitializeComponent();
        _validation = validation; _analysis = analysis; _unavailableReason = unavailableReason;
        YuNetGrid.ItemsSource = _rows;
    }

    private async void CalibrateSFace_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginOperation("正在后台计算 SFace 阈值，请勿重复点击…")) return;
        try
        {
            var result = await UiWork.RunDatabaseAsync(() => _validation.CalibrateSFace());
            var applied = await UiWork.RunDatabaseAsync(() => _validation.GetAppliedSFaceThreshold(result.ModelVersion));
            _lastCalibration = result;
            var ready = result.QualifiedPeople >= 5 ? "样本人数已达标" : $"还需补足 {5 - result.QualifiedPeople} 位达到 20 张照片的成员";
            SFaceResult.Text = $"模型：{result.ModelVersion}\n" +
                $"已确认人物：{result.ConfirmedPeople}；达到 20 张：{result.QualifiedPeople}/5（{ready}）\n" +
                $"已确认人脸：{result.ConfirmedFaces}；同人样本对：{result.SamePairs}；异人样本对：{result.DifferentPairs}\n" +
                $"推荐余弦阈值：{result.RecommendedThreshold:F3}\n" +
                $"误识率：{result.FalseAcceptRate:P2}；漏识率：{result.FalseRejectRate:P2}\n" +
                $"当前应用阈值：{(applied is null ? "尚未应用标定结果" : applied.Value.ToString("F3"))}";
            Status.Text = "SFace 标定完成";
        }
        catch (Exception exception) { Status.Text = $"SFace 标定失败：{exception.Message}"; }
        finally { EndOperation(); }
    }

    private async void ApplySFaceThreshold_Click(object sender, RoutedEventArgs e)
    {
        if (_lastCalibration is null) return;
        if (!TryBeginOperation("正在应用 SFace 阈值…")) return;
        try
        {
            await UiWork.RunDatabaseAsync(() => _validation.ApplySFaceThreshold(_lastCalibration));
            Status.Text = $"已将阈值 {_lastCalibration.RecommendedThreshold:F3} 应用于模型 {_lastCalibration.ModelVersion} 的人物自动匹配";
        }
        catch (Exception exception) { Status.Text = $"应用失败：{exception.Message}"; }
        finally { EndOperation(); }
    }

    private async void LoadYuNet_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginOperation("正在后台加载验证照片…")) return;
        try { await ReloadYuNetRowsAsync(); }
        catch (Exception exception) { Status.Text = $"加载失败：{exception.Message}"; }
        finally { EndOperation(); }
    }

    private async void AnalyzeYuNet_Click(object sender, RoutedEventArgs e)
    {
        if (_analysis is null) { Status.Text = _unavailableReason ?? "人脸模型不可用"; return; }
        if (!TryBeginOperation("正在准备扫描验证照片…")) return;
        try
        {
            if (_rows.Count == 0) await ReloadYuNetRowsAsync(evaluate: false);
            var progress = new Progress<(int Completed, int Total)>(value =>
            {
                WorkProgress.Value = value.Total == 0 ? 0 : value.Completed * 100d / value.Total;
                Status.Text = $"正在扫描验证照片：{value.Completed}/{value.Total}";
            });
            var result = await _analysis.AnalyzeAsync(_rows.Select(row => row.Image), progress);
            await ReloadYuNetRowsAsync();
            Status.Text = $"扫描完成：新增 {result.Analyzed}，缓存复用 {result.Skipped}，失败 {result.Failed}";
        }
        catch (Exception exception) { Status.Text = $"扫描失败：{exception.Message}"; }
        finally { EndOperation(); }
    }

    private async void SaveYuNet_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginOperation("正在保存人工标注…")) return;
        YuNetGrid.CommitEdit(); YuNetGrid.CommitEdit();
        var rows = _rows.Where(row => row.ExpectedFaceCount is not null)
            .Select(row => (row.Image.Id, Expected: row.ExpectedFaceCount!.Value, row.Categories, row.Notes)).ToArray();
        try
        {
            await UiWork.RunDatabaseAsync(() =>
            {
                foreach (var row in rows) _validation.SaveGroundTruth(row.Id, row.Expected, row.Categories, row.Notes);
            });
            Status.Text = $"已保存 {rows.Length} 张照片的人工标注";
            await EvaluateYuNetAsync();
        }
        catch (Exception exception) { Status.Text = $"保存失败：{exception.Message}"; }
        finally { EndOperation(); }
    }

    private async void EvaluateYuNet_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginOperation("正在后台计算漏检率…")) return;
        try { await EvaluateYuNetAsync(); }
        catch (Exception exception) { Status.Text = $"计算失败：{exception.Message}"; }
        finally { EndOperation(); }
    }

    private async Task EvaluateYuNetAsync()
    {
        var result = await UiWork.RunDatabaseAsync(_validation.EvaluateYuNet);
        var target = result.LabeledImages is >= 500 and <= 1000 ? "已达到建议样本规模" : $"建议 500–1000 张，当前还差 {Math.Max(0, 500 - result.LabeledImages)} 张";
        var categories = result.Categories.Count == 0 ? "尚无场景标签" : string.Join("；", result.Categories.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key} {pair.Value.Images}张/漏{pair.Value.MissedFaces}"));
        YuNetResult.Text = $"已标注 {result.LabeledImages} 张（{target}）；人工人脸 {result.ExpectedFaces}，检测 {result.DetectedFaces}，漏检 {result.MissedFaces}，额外检测 {result.ExtraDetections}；漏检率 {result.MissRate:P2}，额外检测率 {result.ExtraDetectionRate:P2}\n分类：{categories}";
    }

    private async Task ReloadYuNetRowsAsync(bool evaluate = true)
    {
        var rows = await UiWork.RunDatabaseAsync(() => _validation.GetYuNetRows().Select(row => new YuNetValidationItem(row)).ToArray());
        _rows.Clear();
        foreach (var batch in rows.Chunk(50))
        {
            foreach (var row in batch) _rows.Add(row);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        }
        WorkProgress.Value = 0;
        Status.Text = $"已加载 {_rows.Count} 张照片";
        if (evaluate) await EvaluateYuNetAsync();
    }

    private void SetBusy(bool busy)
    {
        YuNetGrid.IsEnabled = !busy;
        CalibrateSFaceButton.IsEnabled = !busy;
        LoadYuNetButton.IsEnabled = !busy;
        AnalyzeYuNetButton.IsEnabled = !busy;
        SaveYuNetButton.IsEnabled = !busy;
        EvaluateYuNetButton.IsEnabled = !busy;
        ApplySFaceThreshold.IsEnabled = !busy && _lastCalibration is { SamePairs: > 0, DifferentPairs: > 0, ModelVersion: not "none" };
        WorkProgress.IsIndeterminate = busy;
    }

    private bool TryBeginOperation(string status)
    {
        if (Interlocked.CompareExchange(ref _operationRunning, 1, 0) != 0)
        {
            Status.Text = "已有任务正在执行，请等待完成。";
            return false;
        }
        Status.Text = status;
        SetBusy(true);
        return true;
    }

    private void EndOperation()
    {
        Interlocked.Exchange(ref _operationRunning, 0);
        SetBusy(false);
    }
}

public sealed class YuNetValidationItem(YuNetValidationRow row)
{
    public ImageRecord Image { get; } = row.Image;
    public string FileName => Path.GetFileName(Image.Path);
    public int DetectedFaceCount { get; } = row.DetectedFaceCount;
    public int? ExpectedFaceCount { get; set; } = row.GroundTruth?.ExpectedFaceCount;
    public string Categories { get; set; } = row.GroundTruth?.Categories ?? "";
    public string? Notes { get; set; } = row.GroundTruth?.Notes;
}
