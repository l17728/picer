using System.Windows;
using System.Windows.Controls;
using Picer.App.Infrastructure;
using Picer.Core.Data;
using Picer.Core.Search;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.App.Views;

public sealed class VideoMetadataWindow : Window
{
    public VideoMetadataWindow(PicerDatabase database, long videoId, Action saved)
    {
        Title = "视频标签与视觉概念"; Width = 460; Height = 550;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(16) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var status = new TextBlock { Text = "正在加载…", TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(status);
        Loaded += async (_, _) =>
        {
            try
            {
                var repository = new VideoSearchMetadata(database);
                var data = await UiWork.RunDatabaseAsync(() => (Tags: SemanticEngine.GetTags(database).ToArray(),
                    Assigned: repository.GetTags(videoId), Concepts: repository.GetConcepts(videoId)));
                status.Text = "标签和已确认概念参与视频组合筛选；取消概念勾选会排除该建议。";
                panel.Children.Add(new TextBlock { Text = "标签", Margin = new Thickness(0, 10, 0, 4) });
                var tags = data.Tags.Select(tag => (tag.Id, Box: new CheckBox { Content = tag.Name, IsChecked = data.Assigned.Contains(tag.Id) })).ToArray();
                foreach (var tag in tags) panel.Children.Add(tag.Box);
                panel.Children.Add(new TextBlock { Text = "视觉概念", Margin = new Thickness(0, 10, 0, 4) });
                var concepts = VisualConceptCatalog.DisplayNames.Select(concept => (concept.Key,
                    Box: new CheckBox { Content = concept.Value, IsChecked = data.Concepts.Contains(concept.Key) })).ToArray();
                foreach (var concept in concepts) panel.Children.Add(concept.Box);
                var save = new Button { Content = "保存", Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(10) };
                panel.Children.Add(save);
                save.Click += async (_, _) =>
                {
                    save.IsEnabled = false;
                    try
                    {
                        var tagValues = tags.Select(item => (item.Id, Assigned: item.Box.IsChecked == true)).ToArray();
                        var conceptValues = concepts.Select(item => (item.Key, Assigned: item.Box.IsChecked == true)).ToArray();
                        await UiWork.RunDatabaseAsync(() =>
                        {
                            foreach (var tag in tagValues.Where(item => data.Assigned.Contains(item.Id) != item.Assigned)) repository.SetTag(videoId, tag.Id, tag.Assigned);
                            foreach (var concept in conceptValues.Where(item => data.Concepts.Contains(item.Key) != item.Assigned)) repository.SetConcept(videoId, concept.Key, "manual", concept.Assigned ? "accepted" : "rejected");
                        });
                        saved(); DialogResult = true;
                    }
                    catch (Exception exception) { status.Text = "保存失败：" + exception.Message; save.IsEnabled = true; }
                };
            }
            catch (Exception exception) { status.Text = "加载失败：" + exception.Message; }
        };
    }
}
