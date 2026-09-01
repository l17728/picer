using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace Picer.App.Views;

public partial class DataProtectionWindow : Window
{
    private readonly DataProtectionService _service;
    public DataProtectionWindow(DataProtectionService service) { InitializeComponent(); _service = service; RootText.Text = service.DataRoot; StatusText.Text = "正在后台统计空间…"; Loaded += async (_, _) => await RefreshAreasAsync(); }
    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "保存 Picer 备份", Filter = "Picer 备份 (*.picer-backup)|*.picer-backup", FileName = $"Picer-{DateTime.Now:yyyyMMdd-HHmm}.picer-backup", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        SetBusy(true);
        try { await _service.CreateBackupAsync(dialog.FileName, new Progress<string>(x => StatusText.Text = x)); StatusText.Text = $"备份完成：{dialog.FileName}"; }
        catch (Exception ex) { StatusText.Text = $"备份失败：{ex.Message}"; } finally { SetBusy(false); }
    }
    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 Picer 备份", Filter = "Picer 备份 (*.picer-backup)|*.picer-backup" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var manifest = DataProtectionService.ValidateBackup(dialog.FileName);
            var answer = MessageBox.Show($"备份时间：{manifest.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}\n数据库版本：v{manifest.DatabaseVersion}\n\n恢复将重启 Picer，并在当前数据目录保留恢复前快照。继续吗？", "确认恢复", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
            DataProtectionService.ScheduleRestore(dialog.FileName, _service.DataRoot);
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--wait-for-pid {Environment.ProcessId}") { UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (Exception ex) { StatusText.Text = $"备份校验失败：{ex.Message}"; }
    }
    private void Migrate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择新的空数据目录", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (MessageBox.Show("Picer 将重启并复制全部数据到新目录。迁移成功后原目录会保留，确认运行正常后可自行归档。现在继续吗？", "确认迁移", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
            DataProtectionService.ScheduleMigration(_service.DataRoot, dialog.FolderName);
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--wait-for-pid {Environment.ProcessId}") { UseShellExecute = true }); Application.Current.Shutdown();
        }
        catch (Exception ex) { StatusText.Text = $"无法安排迁移：{ex.Message}"; }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAreasAsync();
    private async Task RefreshAreasAsync() { Areas.ItemsSource = await Task.Run(() => _service.GetAreaSizes().Select(x => new AreaRow(x.Name, x.Path, FormatBytes(x.Bytes))).ToArray()); StatusText.Text = "空间占用已刷新。"; }
    private void SetBusy(bool value) { BackupButton.IsEnabled = !value; RestoreButton.IsEnabled = !value; MigrateButton.IsEnabled = !value; Progress.IsIndeterminate = value; }
    private static string FormatBytes(long bytes) { string[] units = ["B", "KB", "MB", "GB", "TB"]; var size = (double)bytes; var index = 0; while (size >= 1024 && index < units.Length - 1) { size /= 1024; index++; } return $"{size:F1} {units[index]}"; }
    private sealed record AreaRow(string Name, string Path, string SizeText);
}
