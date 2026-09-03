using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Picer.Core.Models;
using Picer.Core.Vision;
using Picer.App.Infrastructure;
using Picer.Core.Logging;
using System.Diagnostics;

namespace Picer.App.Views;

public partial class FaceValidationWindow : Window
{
    private readonly FaceValidationService _validation;
    private readonly FaceTrainingService _training;
    private readonly VisionAnalysisService? _analysis;
    private readonly string? _unavailableReason;
    private readonly ObservableCollection<YuNetValidationItem> _rows = [];
    private SFaceCalibrationResult? _lastCalibration;
    private int _operationRunning;
    private CancellationTokenSource? _trainingCancellation;

    public FaceValidationWindow(FaceValidationService validation, FaceTrainingService training, VisionAnalysisService? analysis, string? unavailableReason)
    {
        InitializeComponent();
        _validation = validation; _training = training; _analysis = analysis; _unavailableReason = unavailableReason;
        YuNetGrid.ItemsSource = _rows;
    }

    private async void CalibrateSFace_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginOperation("正在后台计算 SFace 阈值，请勿重复点击…")) return;
        var stopwatch = Stopwatch.StartNew();
        SFaceProgressPanel.Visibility = Visibility.Visible;
        SFaceProgress.IsIndeterminate = true;
        SFaceProgress.Value = 0;
        SFaceProgressText.Text = "SFace：正在启动计算…";
        CalibrateSFaceButton.Content = "正在计算…";
        AppLogger.Info("SFace 标定已触发，正在等待后台计算");
        try
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            var progress = new Progress<(string Stage, int Completed, int Total)>(value =>
            {
                WorkProgress.IsIndeterminate = value.Total <= 1 && value.Completed == 0;
                if (value.Total > 1) WorkProgress.Value = value.Completed * 100d / value.Total;
                SFaceProgress.IsIndeterminate = value.Total <= 1 && value.Completed == 0;
                if (value.Total > 1) SFaceProgress.Value = value.Completed * 100d / value.Total;
                var message = value.Total > 1
                    ? $"SFace：{value.Stage} {value.Completed}/{value.Total}"
                    : $"SFace：{value.Stage}";
                Status.Text = message;
                SFaceProgressText.Text = message;
            });
            var result = await UiWork.RunDatabaseAsync("FaceValidation.CalibrateSFace",
                () => _validation.CalibrateSFace(progress: progress));
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
            SFaceProgress.IsIndeterminate = false;
            SFaceProgress.Value = 100;
            SFaceProgressText.Text = $"SFace：计算完成（{stopwatch.Elapsed.TotalSeconds:F1} 秒）";
            AppLogger.Info($"SFace 标定完成：faces={result.ConfirmedFaces}; same={result.SamePairs}; different={result.DifferentPairs}; elapsed={stopwatch.ElapsedMilliseconds}ms");
        }
        catch (Exception exception)
        {
            Status.Text = $"SFace 标定失败：{exception.Message}";
            SFaceProgress.IsIndeterminate = false;
            SFaceProgress.Value = 0;
            SFaceProgressText.Text = $"SFace：计算失败——{exception.Message}";
            AppLogger.Error("SFace 标定失败", exception);
        }
        finally
        {
            CalibrateSFaceButton.Content = "计算推荐阈值与误识率";
            EndOperation();
        }
    }

    private async void ApplySFaceThreshold_Click(object sender, RoutedEventArgs e)
    {
        if (_lastCalibration is null)
        {
            MessageBox.Show("请先点击“计算推荐阈值与误识率”。", "应用 SFace 阈值",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!TryBeginOperation("正在应用 SFace 阈值…")) return;
        SFaceProgressPanel.Visibility = Visibility.Visible;
        SFaceProgress.IsIndeterminate = true;
        SFaceProgressText.Text = "SFace：正在保存并验证新阈值…";
        ApplySFaceThreshold.Content = "正在应用…";
        try
        {
            await UiWork.RunDatabaseAsync(() => _validation.ApplySFaceThreshold(_lastCalibration));
            var applied = await UiWork.RunDatabaseAsync(() =>
                _validation.GetAppliedSFaceThreshold(_lastCalibration.ModelVersion));
            if (applied is null || Math.Abs(applied.Value - _lastCalibration.RecommendedThreshold) > 0.000_001)
                throw new InvalidOperationException("数据库回读的阈值与待应用值不一致");
            var message = $"阈值 {applied.Value:F3} 已成功应用到模型 {_lastCalibration.ModelVersion}。";
            Status.Text = message;
            SFaceProgress.IsIndeterminate = false;
            SFaceProgress.Value = 100;
            SFaceProgressText.Text = $"SFace：应用成功 · 当前阈值 {applied.Value:F3}";
            AppLogger.Info($"SFace 阈值应用成功：model={_lastCalibration.ModelVersion}; threshold={applied.Value:F3}");
            MessageBox.Show($"{message}\n\n现有人工分类不会改变。若要按新标准重算尚未确认的人脸，请返回人物页点击“更新自动归类”。",
                "SFace 阈值应用成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            Status.Text = $"应用失败：{exception.Message}";
            SFaceProgress.IsIndeterminate = false;
            SFaceProgress.Value = 0;
            SFaceProgressText.Text = $"SFace：应用失败——{exception.Message}";
            AppLogger.Error("SFace 阈值应用失败", exception);
            MessageBox.Show($"SFace 阈值没有成功应用：\n{exception.Message}", "应用失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ApplySFaceThreshold.Content = "应用到人物自动匹配";
            EndOperation();
        }
    }

    private async void TrainFace_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginOperation("正在准备训练数据快照…")) return;
        _trainingCancellation = new CancellationTokenSource();
        TrainFaceButton.Content = "训练进行中…";
        CancelTrainingButton.IsEnabled = true;
        SFaceProgressPanel.Visibility = Visibility.Visible;
        try
        {
            var settings = CloudVisionSettings.Load(AppPaths.CloudVisionSettingsPath);
            if (!Uri.TryCreate(settings.FaceTrainingEndpoint, UriKind.Absolute, out var endpoint))
                throw new InvalidOperationException("请先在 AI 模型设置中配置有效的人脸训练提交地址。");
            AppLogger.Info($"FaceTraining submit requested; endpoint={endpoint.GetLeftPart(UriPartial.Authority)}; no image paths or names will be sent");
            var progress = new Progress<FaceTrainingProgress>(value =>
            {
                SFaceProgress.IsIndeterminate = false;
                SFaceProgress.Value = value.Percent;
                WorkProgress.IsIndeterminate = false;
                WorkProgress.Value = value.Percent;
                SFaceProgressText.Text = $"训练：{value.Detail}";
                Status.Text = $"训练阶段 {value.Stage} · {value.Percent}%";
            });
            var result = await _training.SubmitAndApplyAsync(endpoint, progress, _trainingCancellation.Token);
            SFaceResult.Text = $"训练快照：{result.DatasetId}\n任务：{result.JobId}\n制品：{result.ArtifactId}\n" +
                $"样本：{result.Samples}；人物：{result.People}\n误识率：{result.FalseAcceptRate:P2}；漏识率：{result.FalseRejectRate:P2}\n{result.Message}";
            Status.Text = result.Message;
            AppLogger.Info($"FaceTraining completed; dataset={result.DatasetId}; job={result.JobId}; artifact={result.ArtifactId}; applied={result.Applied}; far={result.FalseAcceptRate}; frr={result.FalseRejectRate}");
        }
        catch (OperationCanceledException)
        {
            Status.Text = "训练已取消；当前生效模型未改变。";
            AppLogger.Info("FaceTraining canceled by user");
        }
        catch (Exception exception)
        {
            Status.Text = $"训练失败：{exception.Message}";
            AppLogger.Error("FaceTraining failed", exception);
        }
        finally
        {
            _trainingCancellation?.Dispose(); _trainingCancellation = null;
            TrainFaceButton.Content = "提交训练并自动应用";
            CancelTrainingButton.IsEnabled = false;
            EndOperation();
        }
    }

    private void CancelTraining_Click(object sender, RoutedEventArgs e)
    {
        Status.Text = "正在取消训练…";
        _trainingCancellation?.Cancel();
    }

    private async void RollbackTraining_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginOperation("正在回滚上一版人脸增强策略…")) return;
        try
        {
            var artifact = await UiWork.RunDatabaseAsync(_training.Rollback);
            Status.Text = $"已回滚并应用上一版制品 {artifact}；人工分类未改变。";
            AppLogger.Info($"FaceTraining rollback applied; artifact={artifact}");
        }
        catch (Exception exception) { Status.Text = $"回滚失败：{exception.Message}"; AppLogger.Error("FaceTraining rollback failed", exception); }
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
        TrainFaceButton.IsEnabled = !busy;
        RollbackTrainingButton.IsEnabled = !busy;
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
