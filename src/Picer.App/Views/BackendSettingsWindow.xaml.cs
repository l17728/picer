using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows;

namespace Picer.App.Views;

public partial class BackendSettingsWindow : Window
{
    public BackendSettingsWindow()
    {
        InitializeComponent();
        var backend = AppPaths.LoadIndexBackend();
        if (AppServices.ServiceMode)
        {
            RemoteModeBox.IsChecked = true;
            IpBox.Text = Uri.TryCreate(backend.BaseUrl, UriKind.Absolute, out var uri) ? uri.Host : "";
        }
        else
        {
            LocalModeBox.IsChecked = true;
            IpBox.Text = "124.156.193.122";
        }
        UpdateIpState();
    }

    private void Mode_Checked(object sender, RoutedEventArgs e) => UpdateIpState();
    private void UpdateIpState() => IpBox.IsEnabled = RemoteModeBox.IsChecked == true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RemoteModeBox.IsChecked == true)
            {
                var ip = IpBox.Text.Trim();
                if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) throw new InvalidOperationException("请输入有效的 IPv4 地址。");
                if (!File.Exists(AppPaths.ServiceEndpointPath)) throw new InvalidOperationException("缺少 service-endpoint.json，请先完成 IndexService 配对。");
                AppPaths.SaveIndexBackendService(ip);
            }
            else AppPaths.SaveIndexBackendLocal();

            var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位当前程序。");
            Process.Start(new ProcessStartInfo(processPath, $"--wait-for-pid {Environment.ProcessId}") { UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (Exception exception) { ErrorText.Text = exception.Message; }
    }
}
