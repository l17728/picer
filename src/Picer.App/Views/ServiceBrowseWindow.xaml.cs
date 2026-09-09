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
}
