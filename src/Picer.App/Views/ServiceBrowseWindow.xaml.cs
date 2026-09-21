using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
}
