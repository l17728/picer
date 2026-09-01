using System.Windows;
using System.Windows.Controls;
using Picer.Core.Models;

namespace Picer.App.Views;

public sealed class EditLocationWindow : Window
{
    private readonly ComboBox _locations;
    private readonly TextBox _childName;
    public LocationHierarchyChoice? Result { get; private set; }

    public EditLocationWindow(IReadOnlyList<LocationHierarchyChoice> choices, int photoCount = 1)
    {
        Title = photoCount > 1 ? $"批量编辑 {photoCount} 张照片的地点" : "编辑照片地点";
        Width = 560;
        Height = 245;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = photoCount > 1 ? $"将选中的 {photoCount} 张照片统一归入已有地理节点" : "归入已有地理节点",
            FontWeight = FontWeights.SemiBold
        });
        _locations = new ComboBox
        {
            ItemsSource = choices, DisplayMemberPath = nameof(LocationHierarchyChoice.DisplayName),
            Margin = new Thickness(0, 6, 0, 12), IsTextSearchEnabled = true
        };
        if (choices.Count > 0) _locations.SelectedIndex = 0;
        panel.Children.Add(_locations);
        panel.Children.Add(new TextBlock
        {
            Text = "可选：在所选节点下增加一个子节点（留空则直接归入所选节点）",
            TextWrapping = TextWrapping.Wrap
        });
        _childName = new TextBox { Margin = new Thickness(0, 6, 0, 14) };
        panel.Children.Add(_childName);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(6, 0, 0, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        var save = new Button { Content = photoCount > 1 ? $"应用到 {photoCount} 张照片" : "保存地点", Padding = new Thickness(16, 5, 16, 5), IsDefault = true };
        save.Click += Save;
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        if (_locations.SelectedItem is not LocationHierarchyChoice selected)
        {
            MessageBox.Show("请先选择一个已有地理节点。", "编辑地点", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Result = string.IsNullOrWhiteSpace(_childName.Text) ? selected : selected.AddChild(_childName.Text);
        DialogResult = true;
    }
}
