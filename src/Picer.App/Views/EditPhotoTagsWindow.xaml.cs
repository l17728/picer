using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Picer.Core.Search;

namespace Picer.App.Views;

public partial class EditPhotoTagsWindow : Window
{
    public ObservableCollection<PhotoTagEditItem> Items { get; }

    public EditPhotoTagsWindow(int photoCount, IEnumerable<(Tag Tag, int AssignedCount)> tags)
    {
        InitializeComponent();
        Items = new(tags.Select(value => new PhotoTagEditItem(value.Tag,
            value.AssignedCount == 0 ? false : value.AssignedCount == photoCount ? true : null)));
        DataContext = this;
        HelpText.Text = photoCount == 1
            ? "勾选要应用到该照片的标签。"
            : $"正在编辑 {photoCount} 张照片：半选表示仅部分照片已有该标签，保持半选不会改变现状。";
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var name = NewTagText.Text.Trim();
        if (name.Length == 0) return;
        var existing = Items.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (existing is not null) existing.IsChecked = true;
        else Items.Add(new PhotoTagEditItem(new Tag { Name = name }, true) { IsNew = true });
        NewTagText.Clear();
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

public partial class PhotoTagEditItem(Tag tag, bool? isChecked) : ObservableObject
{
    public Tag Tag { get; } = tag;
    public string Name => Tag.Name;
    public bool IsNew { get; set; }
    [ObservableProperty] private bool? _isChecked = isChecked;
}
