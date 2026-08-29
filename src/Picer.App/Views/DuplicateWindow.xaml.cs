using System.Windows;
using Picer.App.ViewModels;

namespace Picer.App.Views;

public partial class DuplicateWindow : Window
{
    public DuplicateWindow(DuplicateViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
