using System.Windows;
using System.Windows.Controls;
using Picer.App.Infrastructure;
using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.App.Views;

public sealed class ContentReferencesWindow : Window
{
    public ContentReferencesWindow(string sha256)
    {
        Title = "同一照片的所有位置"; Width = 760; Height = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new DockPanel { Margin = new Thickness(16) };
        var status = new TextBlock { Text = "读取位置引用…", Margin = new Thickness(0,0,0,12) };
        DockPanel.SetDock(status, Dock.Top); layout.Children.Add(status);
        var list = new ListBox(); layout.Children.Add(list); Content = layout;
        Loaded += async (_, _) =>
        {
            try
            {
                var data = await UiWork.RunDatabaseAsync(() =>
                {
                    var repository = new ContentReferenceRepository(AppServices.Db);
                    return (Counts: repository.GetCounts(sha256), References: repository.GetReferences(sha256));
                });
                status.Text = $"1 份内容索引；可用位置 {data.Counts.Available}，已删除或移动 {data.Counts.Missing}，共 {data.Counts.Total} 个引用。";
                list.ItemsSource = data.References.Select(image => $"{(image.Status == ImageStatus.Active ? "可用" : "已删除或移动")}　{image.Path}");
            }
            catch (Exception exception) { status.Text = "读取失败：" + exception.Message; }
        };
    }
}
