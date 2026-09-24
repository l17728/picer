using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Picer.App.Controls;
using Picer.App.ViewModels;
using Picer.Core.Gateway;

namespace Picer.App.Views;

public partial class ServiceBrowseWindow : Window
{
    public ServiceBrowseViewModel ViewModel { get; }

    public ServiceBrowseWindow() : this(AppServices.Gateway, true)
    {
    }

    public ServiceBrowseWindow(IIndexGateway gateway, bool autoLoad)
    {
        InitializeComponent();
        ViewModel = new ServiceBrowseViewModel(gateway);
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
        count.SetValue(TextBlock.ForegroundProperty, System.Windows.Media.Brushes.Gray);
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
