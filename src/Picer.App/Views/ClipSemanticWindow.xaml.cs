using System.IO;
using System.Windows;
using Picer.Core.SemanticSearch;

namespace Picer.App.Views;

public partial class ClipSemanticWindow : Window
{
    private readonly ClipSemanticService? _service; private readonly ClipSemanticRepository _repository; private readonly string? _reason;
    private CancellationTokenSource? _cancellation; private List<ResultRow> _rows = [];
    public ClipSemanticWindow(ClipSemanticService? service, ClipSemanticRepository repository, string? reason)
    { InitializeComponent(); _service = service; _repository = repository; _reason = reason; StatusText.Text = service is null ? reason ?? "中文语义模型不可用" : $"模型已就绪：{service.ModelVersion}"; BuildButton.IsEnabled = service is not null; }

    private async void Build_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null) { MessageBox.Show(_reason, "模型不可用"); return; }
        _cancellation = new(); BuildButton.IsEnabled = false; CancelButton.IsEnabled = true;
        try { var report = new Progress<ClipIndexProgress>(p => { Progress.Value = p.Total == 0 ? 0 : p.Completed * 100d / p.Total; StatusText.Text = $"正在建立索引 {p.Completed}/{p.Total}：{p.CurrentFile}"; }); await _service.BuildIndexAsync(report, _cancellation.Token); StatusText.Text = "语义索引已完成；未变化照片会复用已有向量。"; }
        catch (OperationCanceledException) { StatusText.Text = "已取消；下次可继续。"; } catch (Exception ex) { StatusText.Text = $"索引失败：{ex.Message}"; }
        finally { BuildButton.IsEnabled = true; CancelButton.IsEnabled = false; _cancellation.Dispose(); _cancellation = null; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();
    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || string.IsNullOrWhiteSpace(QueryBox.Text)) return;
        try { StatusText.Text = "正在本地检索……"; var hits = await _service.SearchAsync(QueryBox.Text.Trim()); _rows = hits.Select(x => new ResultRow(x)).ToList(); Results.ItemsSource = _rows; StatusText.Text = hits.Count == 0 ? "尚无语义索引，请先建立索引。" : $"已返回 {hits.Count} 个最相近结果。"; }
        catch (Exception ex) { StatusText.Text = $"搜索失败：{ex.Message}"; }
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || _rows.Count == 0) return;
        int? expected = int.TryParse(ExpectedCount.Text, out var value) && value > 0 ? value : null;
        _repository.SaveEvaluation(QueryBox.Text.Trim(), _service.ModelVersion, expected, _rows.Select(x => (x.Hit, x.Relevant)));
        var labelled = _rows.Count(x => x.Relevant.HasValue); var relevant = _rows.Count(x => x.Relevant == true); var precision = labelled == 0 ? 0 : relevant / (double)labelled;
        MetricsText.Text = expected is null ? $"已保存：Precision@20={precision:P1}（标注 {labelled}，相关 {relevant}）；填写已知相关总数可计算 Recall@20。" : $"已保存：Precision@20={precision:P1}；Recall@20={Math.Min(1, relevant / (double)expected.Value):P1}。";
    }
    private sealed class ResultRow
    {
        public ResultRow(ClipSearchHit hit) => Hit = hit;
        public ClipSearchHit Hit { get; } public int Rank => Hit.Rank; public string Path => Hit.Image.Path; public string PreviewPath => File.Exists(Hit.Image.ThumbPath) ? Hit.Image.ThumbPath! : Hit.Image.Path;
        public string ScoreText => Hit.Score.ToString("F3"); public string Explanation => $"中文查询与图片语义向量相似度 {Hit.Score:F3}"; public bool? Relevant { get; set; }
    }
}
