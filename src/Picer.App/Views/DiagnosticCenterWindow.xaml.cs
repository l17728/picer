using System.Windows;
using Microsoft.Win32;

namespace Picer.App.Views;

public partial class DiagnosticCenterWindow : Window
{
    private readonly DiagnosticService _service;
    public DiagnosticCenterWindow(DiagnosticService service) { InitializeComponent(); _service = service; RefreshCaches(); StatusText.Text = "尚未运行健康检查。"; }
    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true); try { var report = await _service.BuildReportAsync(new Progress<string>(x => StatusText.Text = x)); Components.ItemsSource = report.Components.Select(x => new ComponentRow(x.Name, x.Ready, x.State, FormatBytes(x.Bytes), x.License)).ToArray(); StatusText.Text = $"检查完成：数据库 v{report.DatabaseVersion}，完整性 {report.DatabaseIntegrity}；组件 {report.Components.Count(x => x.Ready)}/{report.Components.Count} 就绪。"; } catch (Exception ex) { StatusText.Text = $"健康检查失败：{ex.Message}"; } finally { SetBusy(false); }
    }
    private async void Json_Click(object sender, RoutedEventArgs e) { var path = Save("JSON 报告|*.json", "picer-diagnostic.json"); if (path is null) return; SetBusy(true); try { await _service.ExportJsonAsync(path); StatusText.Text = $"JSON 报告已导出：{path}"; } catch (Exception ex) { StatusText.Text = ex.Message; } finally { SetBusy(false); } }
    private async void Csv_Click(object sender, RoutedEventArgs e) { var path = Save("CSV 报告|*.csv", "picer-diagnostic.csv"); if (path is null) return; SetBusy(true); try { await _service.ExportCsvAsync(path); StatusText.Text = $"CSV 报告已导出：{path}"; } catch (Exception ex) { StatusText.Text = ex.Message; } finally { SetBusy(false); } }
    private async void Package_Click(object sender, RoutedEventArgs e) { var path = Save("诊断包|*.zip", "picer-diagnostic-redacted.zip"); if (path is null) return; SetBusy(true); try { await _service.CreateDiagnosticPackageAsync(path); StatusText.Text = $"脱敏诊断包已生成：{path}"; } catch (Exception ex) { StatusText.Text = ex.Message; } finally { SetBusy(false); } }
    private void RefreshCaches_Click(object sender, RoutedEventArgs e) => RefreshCaches();
    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        if (Caches.SelectedItem is not CacheRow row || !row.CanClear) { StatusText.Text = "请选择允许清理的缓存区域。"; return; }
        if (MessageBox.Show($"确认清理“{row.Name}”？这些文件可重新生成，但首次重新访问会变慢。", "确认清理缓存", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { _service.ClearCache(row.Id); RefreshCaches(); StatusText.Text = $"已清理：{row.Name}。"; } catch (Exception ex) { StatusText.Text = $"清理失败：{ex.Message}"; }
    }
    private void RefreshCaches() { Caches.ItemsSource = _service.GetCaches().Select(x => new CacheRow(x.Id, x.Name, x.Path, FormatBytes(x.Bytes), x.CanClear, x.Note)).ToArray(); }
    private void SetBusy(bool busy) { CheckButton.IsEnabled = !busy; Progress.IsIndeterminate = busy; }
    private string? Save(string filter, string name) { var dialog = new SaveFileDialog { Filter = filter, FileName = name, AddExtension = true }; return dialog.ShowDialog(this) == true ? dialog.FileName : null; }
    private static string FormatBytes(long bytes) { string[] units = ["B", "KB", "MB", "GB", "TB"]; var value = (double)bytes; var index = 0; while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; } return $"{value:F1} {units[index]}"; }
    private sealed record ComponentRow(string Name, bool Ready, string State, string SizeText, string License);
    private sealed record CacheRow(string Id, string Name, string Path, string SizeText, bool CanClear, string Note);
}
