using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using Picer.App.Controls;
using Picer.App.ViewModels;
using Picer.Core.Gateway;

namespace Picer.App.Views;

public partial class ServiceBrowseWindow : Window
{
    private async void RepairIndex_Click(object sender, RoutedEventArgs e)
    {
        new IndexRepairWindow { Owner = this }.ShowDialog();
        await ViewModel.SearchAsync();
    }

    private async void PurgeReference_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ServicePhotoItem { IsMissing: true, IsVideo: false } item } button) return;
        if (MessageBox.Show(this, "彻底清除此位置记录和关联缓存？其他位置仍引用的共享内容保留；最后一个引用删除后清除共享索引。",
            "彻底删除此位置记录", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        button.IsEnabled = false;
        try
        {
            var record = await AppServices.Gateway.GetImageAsync(item.MediaId);
            if (record is not null) await AppServices.PurgeMissingImageAsync(record);
            ViewModel.Items.Remove(item);
        }
        catch (Exception exception) { MessageBox.Show(this, "删除未完成，可重试：" + exception.Message); }
        finally { button.IsEnabled = true; }
    }
    public ServiceBrowseViewModel ViewModel { get; }

    public ServiceBrowseWindow() : this(AppServices.Gateway, true)
    {
    }

    public ServiceBrowseWindow(IIndexGateway gateway, bool autoLoad)
    {
        InitializeComponent();
        ViewModel = new ServiceBrowseViewModel(gateway, AppServices.GetClientServices());
        DataContext = ViewModel;
        // The remote tree is hierarchical even though the legacy XAML template
        // predates child directories.  Install the same hierarchical template
        // at runtime so the original window can expand every indexed folder.
        SourcesTree.ItemTemplate = new HierarchicalDataTemplate(typeof(RemoteDirectoryNode))
        {
            ItemsSource = new Binding(nameof(RemoteDirectoryNode.Children)),
            VisualTree = CreateDirectoryTemplate()
        };
        if (autoLoad) Loaded += async (_, _) => await ViewModel.SearchAsync();
    }

    private void Browse_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!e.Handled) e.Handled = HandleShortcut(e.Key, Keyboard.Modifiers);
    }

    internal bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        if (modifiers == ModifierKeys.Control && key == Key.F)
        {
            SearchBox.Focus(); SearchBox.SelectAll(); return true;
        }
        if (modifiers == ModifierKeys.Control && key == Key.L) { SourcesTree.Focus(); return true; }
        if (modifiers != ModifierKeys.None) return false;
        if (key == Key.Escape && SearchBox.IsKeyboardFocusWithin)
        {
            SearchBox.Clear();
            ViewModel.SearchCommand.Execute(null);
            return true;
        }
        if (key == Key.Enter && Photos.IsKeyboardFocusWithin && Photos.SelectedItem is ServicePhotoItem item)
        {
            if (ViewModel.OpenOriginalCommand.CanExecute(item)) ViewModel.OpenOriginalCommand.Execute(item);
            return true;
        }
        return false;
    }

    private static FrameworkElementFactory CreateDirectoryTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetValue(TextBlock.TextProperty, "📁");
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 5, 0));
        panel.AppendChild(icon);
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(RemoteDirectoryNode.Name)));
        name.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(RemoteDirectoryNode.FullPath)));
        panel.AppendChild(name);
        var count = new FrameworkElementFactory(typeof(TextBlock));
        count.SetBinding(TextBlock.TextProperty, new Binding(nameof(RemoteDirectoryNode.ImageCount)) { StringFormat = " ({0})" });
        count.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.ControlTextBrushKey);
        count.SetValue(FrameworkElement.MarginProperty, new Thickness(5, 0, 0, 0));
        panel.AppendChild(count);
        return panel;
    }

    private async void SourcesTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is RemoteDirectoryNode directory)
            await ViewModel.SelectDirectoryAsync(directory);
    }

    private async void MediaFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || sender is not ComboBox box || box.SelectedIndex < 0) return;
        await ViewModel.SetMediaFilterAsync(box.SelectedIndex);
    }

    private void Photos_Loaded(object sender, RoutedEventArgs e) => ScheduleThumbnailPrefetch();
    private void Photos_SizeChanged(object sender, SizeChangedEventArgs e) => ScheduleThumbnailPrefetch();
    private void Photos_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.VerticalChange) > .01 || Math.Abs(e.HorizontalChange) > .01)
            ScheduleThumbnailPrefetch();
    }

    private void ScheduleThumbnailPrefetch()
    {
        var panel = FindVisualChild<VirtualizingWrapPanel>(Photos);
        var range = panel?.GetViewportItemRange() ?? (-1, -1);
        var visible = range.First >= 0
            ? Enumerable.Range(range.First, Math.Max(0, range.Last - range.First + 1))
            : Enumerable.Empty<int>();
        ViewModel.ScheduleThumbnailPrefetch(visible);
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
