using System.Windows;
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
        if (autoLoad) Loaded += async (_, _) => await ViewModel.SearchAsync();
    }

    private async void SourcesTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is Picer.Core.Models.SourceFolder source)
            await ViewModel.SelectSourceAsync(source);
    }
}
