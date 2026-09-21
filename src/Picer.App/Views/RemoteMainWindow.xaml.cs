using System.Windows;

namespace Picer.App.Views;

public partial class RemoteMainWindow : Window
{
    private ServiceBrowseWindow? _browse;

    public RemoteMainWindow() => InitializeComponent();

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var health = await AppServices.Gateway.GetHealthAsync();
            StatusText.Text = $"IndexService 已连接：schema v{health.SchemaVersion}，服务版本 {health.ServiceVersion}";
            Browse_Click(this, new RoutedEventArgs());
        }
        catch (Exception exception) { StatusText.Text = $"IndexService 连接失败：{exception.Message}"; }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_browse is { IsVisible: true }) { _browse.Activate(); return; }
        _browse = new ServiceBrowseWindow { Owner = this };
        _browse.Closed += (_, _) => _browse = null;
        _browse.Show();
    }

    private void ServiceStatus_Click(object sender, RoutedEventArgs e) => new IndexServiceWindow { Owner = this }.ShowDialog();
    private void BackendSettings_Click(object sender, RoutedEventArgs e) => new BackendSettingsWindow { Owner = this }.ShowDialog();
    private void RefreshCache_Click(object sender, RoutedEventArgs e) => StatusText.Text = "远程缓存由内容哈希管理；重新打开媒体时会自动复用。";
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
