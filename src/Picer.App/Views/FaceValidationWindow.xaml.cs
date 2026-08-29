using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Picer.Core.Models;
using Picer.Core.Vision;

namespace Picer.App.Views;

public partial class FaceValidationWindow : Window
{
    private readonly FaceValidationService _validation;
    private readonly VisionAnalysisService? _analysis;
    private readonly string? _unavailableReason;
    private readonly ObservableCollection<YuNetValidationItem> _rows = [];
    private SFaceCalibrationResult? _lastCalibration;

    public FaceValidationWindow(FaceValidationService validation, VisionAnalysisService? analysis, string? unavailableReason)
    {
        InitializeComponent();
        _validation = validation; _analysis = analysis; _unavailableReason = unavailableReason;
        YuNetGrid.ItemsSource = _rows;
    }

    private void CalibrateSFace_Click(object sender, RoutedEventArgs e)
    {
        var result = _validation.CalibrateSFace();
        _lastCalibration = result;
        ApplySFaceThreshold.IsEnabled = result.SamePairs > 0 && result.DifferentPairs > 0 && result.ModelVersion != "none";
        var applied = _validation.GetAppliedSFaceThreshold(result.ModelVersion);
        var ready = result.QualifiedPeople >= 5 ? "样本人数已达标" : $"还需补足 {5 - result.QualifiedPeople} 位达到 20 张照片的成员";
        SFaceResult.Text = $"模型：{result.ModelVersion}\n" +
            $"已确认人物：{result.ConfirmedPeople}；达到 20 张：{result.QualifiedPeople}/5（{ready}）\n" +
            $"已确认人脸：{result.ConfirmedFaces}；同人样本对：{result.SamePairs}；异人样本对：{result.DifferentPairs}\n" +
            $"推荐余弦阈值：{result.RecommendedThreshold:F3}\n" +
            $"误识率：{result.FalseAcceptRate:P2}；漏识率：{result.FalseRejectRate:P2}\n" +
            $"当前应用阈值：{(applied is null ? "尚未应用标定结果" : applied.Value.ToString("F3"))}";
        Status.Text = "SFace 标定完成";
    }

    private void ApplySFaceThreshold_Click(object sender, RoutedEventArgs e)
    {
        if (_lastCalibration is null) return;
        try
        {
            _validation.ApplySFaceThreshold(_lastCalibration);
            Status.Text = $"已将阈值 {_lastCalibration.RecommendedThreshold:F3} 应用于模型 {_lastCalibration.ModelVersion} 的人物自动匹配";
            CalibrateSFace_Click(sender, e);
        }
        catch (Exception exception) { Status.Text = $"应用失败：{exception.Message}"; }
    }

    private void LoadYuNet_Click(object sender, RoutedEventArgs e) => ReloadYuNetRows();

    private async void AnalyzeYuNet_Click(object sender, RoutedEventArgs e)
    {
        if (_analysis is null) { Status.Text = _unavailableReason ?? "人脸模型不可用"; return; }
        if (_rows.Count == 0) ReloadYuNetRows();
        SetBusy(true);
        try
        {
            var progress = new Progress<(int Completed, int Total)>(value =>
            {
                WorkProgress.Value = value.Total == 0 ? 0 : value.Completed * 100d / value.Total;
                Status.Text = $"正在扫描验证照片：{value.Completed}/{value.Total}";
            });
            var result = await _analysis.AnalyzeAsync(_rows.Select(row => row.Image), progress);
            ReloadYuNetRows();
            Status.Text = $"扫描完成：新增 {result.Analyzed}，缓存复用 {result.Skipped}，失败 {result.Failed}";
        }
        catch (Exception exception) { Status.Text = $"扫描失败：{exception.Message}"; }
        finally { SetBusy(false); }
    }

    private void SaveYuNet_Click(object sender, RoutedEventArgs e)
    {
        YuNetGrid.CommitEdit(); YuNetGrid.CommitEdit();
        var saved = 0;
        foreach (var row in _rows.Where(row => row.ExpectedFaceCount is not null))
        {
            _validation.SaveGroundTruth(row.Image.Id, row.ExpectedFaceCount!.Value, row.Categories, row.Notes);
            saved++;
        }
        Status.Text = $"已保存 {saved} 张照片的人工标注";
        EvaluateYuNet();
    }

    private void EvaluateYuNet_Click(object sender, RoutedEventArgs e) => EvaluateYuNet();

    private void EvaluateYuNet()
    {
        var result = _validation.EvaluateYuNet();
        var target = result.LabeledImages is >= 500 and <= 1000 ? "已达到建议样本规模" : $"建议 500–1000 张，当前还差 {Math.Max(0, 500 - result.LabeledImages)} 张";
        var categories = result.Categories.Count == 0 ? "尚无场景标签" : string.Join("；", result.Categories.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key} {pair.Value.Images}张/漏{pair.Value.MissedFaces}"));
        YuNetResult.Text = $"已标注 {result.LabeledImages} 张（{target}）；人工人脸 {result.ExpectedFaces}，检测 {result.DetectedFaces}，漏检 {result.MissedFaces}，额外检测 {result.ExtraDetections}；漏检率 {result.MissRate:P2}，额外检测率 {result.ExtraDetectionRate:P2}\n分类：{categories}";
    }

    private void ReloadYuNetRows()
    {
        _rows.Clear();
        foreach (var row in _validation.GetYuNetRows()) _rows.Add(new YuNetValidationItem(row));
        WorkProgress.Value = 0;
        Status.Text = $"已加载 {_rows.Count} 张照片";
        EvaluateYuNet();
    }

    private void SetBusy(bool busy)
    {
        YuNetGrid.IsEnabled = !busy;
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
