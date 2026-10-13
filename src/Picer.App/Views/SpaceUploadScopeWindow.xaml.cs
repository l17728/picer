using System.Windows;
using Picer.Core.Identity;
using Picer.Core.Models;

namespace Picer.App.Views;

public partial class SpaceUploadScopeWindow : Window
{
    private readonly SourceChoice[] _sources;
    public SpaceUploadScope? Result { get; private set; }
    public bool ServeMedia => ServeMediaBox.IsChecked == true;

    public SpaceUploadScopeWindow(IEnumerable<SourceFolder> sources, DeviceSession session, SpaceUploadScope? previous = null)
    {
        InitializeComponent();
        DestinationText.Text = $"账号：{session.AccountName}；空间：{session.SpaceName}";
        _sources = sources.Select(source => new SourceChoice(source.Id, source.Path, previous?.SourceIds.Contains(source.Id) == true)).ToArray();
        SourcesList.ItemsSource = _sources;
        if (previous is not null)
        {
            PhotosBox.IsChecked = previous.Photos; VideosBox.IsChecked = previous.Videos;
            PeopleBox.IsChecked = previous.People; LocationBox.IsChecked = previous.PreciseLocation;
        }
    }

    private void Upload_Click(object sender, RoutedEventArgs e)
    {
        var selected = _sources.Where(source => source.Selected).Select(source => source.Id).ToArray();
        if (selected.Length == 0 || PhotosBox.IsChecked != true && VideosBox.IsChecked != true)
        { StatusText.Text = "请至少选择一个来源目录以及照片或视频索引。"; return; }
        Result = new(selected, PhotosBox.IsChecked == true, VideosBox.IsChecked == true,
            PeopleBox.IsChecked == true, LocationBox.IsChecked == true);
        DialogResult = true;
    }

    public sealed class SourceChoice(long id, string path, bool selected)
    {
        public long Id { get; } = id;
        public string Path { get; } = path;
        public bool Selected { get; set; } = selected;
    }
}
