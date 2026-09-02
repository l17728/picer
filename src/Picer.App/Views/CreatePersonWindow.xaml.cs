using System.Windows;

namespace Picer.App.Views;

public partial class CreatePersonWindow : Window
{
    public string PersonName => NameText.Text.Trim();
    public CreatePersonWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => NameText.Focus();
    }
    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameText.Text))
        {
            MessageBox.Show("请输入人物名称。", "创建人物", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
