using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;
using Picer.Core.Indexing;
using Picer.Core.Models;

namespace Picer.App;

public partial class MissingFilesWindow : Window
{
    private readonly IndexEngine _engine;
    private readonly ObservableCollection<ImageRecord> _items;

    public MissingFilesWindow(IndexEngine engine, IReadOnlyList<ImageRecord> missing)
    {
        InitializeComponent();
        _engine = engine;
        _items = new ObservableCollection<ImageRecord>(missing);
        MissingList.ItemsSource = _items;
        if (_items.Count > 0)
            MissingList.SelectedItem = _items[0];
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (MissingList.SelectedItem is not ImageRecord rec)
            return;
        if (MessageBox.Show(this, $"确定删除该文件的索引记录吗？\n{rec.Path}",
                "删除记录", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await _engine.RemoveIndexRecordAsync(rec.Id);
        _items.Remove(rec);
    }

    private async void Relocate_Click(object sender, RoutedEventArgs e)
    {
        if (MissingList.SelectedItem is not ImageRecord rec)
            return;
        var dialog = new OpenFileDialog
        {
            Title = "请选择该图片的新位置",
            Filter = SupportedFormats.OpenFileDialogFilter,
            FileName = System.IO.Path.GetFileName(rec.Path)
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var result = await _engine.RelocateAsync(rec.Id, dialog.FileName);
        if (!result.Success)
        {
            var message = result.Failure switch
            {
                RelocateFailure.HashMismatch => "所选图片的内容与原记录不一致。为避免标签和元数据错配，Picer 未更改索引。",
                RelocateFailure.OriginalHashUnavailable => "原记录没有可用于身份校验的哈希。请先重新扫描原来源。",
                RelocateFailure.FileReadFailed => "无法读取所选图片，请检查文件权限或文件是否完整。",
                RelocateFailure.RecordNotFound => "该索引记录已不存在。",
                _ => "所选文件不存在或不是受支持的图片，无法重建索引。"
            };
            MessageBox.Show(this, message, "无法重新定位",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _items.Remove(rec);
    }

    private async void RelocateRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择图片移动后的新根目录" };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            IsEnabled = false;
            var plan = await _engine.BuildRelocationPlanAsync(dialog.FolderName);
            var summary = $"可安全匹配：{plan.Matches.Count}\n冲突：{plan.Conflicts.Count}\n未匹配：{plan.UnmatchedImageIds.Count}\n\n是否恢复可安全匹配的索引？";
            if (plan.Matches.Count == 0 || MessageBox.Show(this, summary, "批量重新定位预览",
                    MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel) != MessageBoxResult.OK)
                return;

            var results = await _engine.ApplyRelocationPlanAsync(plan);
            var restored = results.Where(result => result.Success).Select(result => result.Image!.Id).ToHashSet();
            foreach (var item in _items.Where(item => restored.Contains(item.Id)).ToList()) _items.Remove(item);
            MessageBox.Show(this, $"已恢复 {restored.Count} 个索引；失败 {results.Count - restored.Count} 个。",
                "批量重新定位完成", MessageBoxButton.OK,
                results.All(result => result.Success) ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"扫描新目录失败：{ex.Message}\nPicer 未更改缺失记录。", "批量重新定位失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsEnabled = true; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
