using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using Picer.Core.Gateway;

namespace Picer.App.Views;

public partial class BackendSettingsWindow : Window
{
    private ServiceConnectionSetup? _verified;
    private CancellationTokenSource? _probe;
    private byte[]? _confirmedPin;

    public BackendSettingsWindow()
    {
        InitializeComponent();
        try
        {
            var backend = AppPaths.LoadIndexBackend();
            if (backend.Mode == IndexBackendConfig.ServiceMode && backend.BaseUrl is { Length: > 0 })
            {
                IpBox.Text = backend.BaseUrl;
                TokenBox.Password = backend.Token ?? "";
                PinBox.Text = backend.SpkiPin is null ? "" : Convert.ToHexString(SHA256.HashData(backend.SpkiPin));
                RemoteModeBox.IsChecked = true;
            }
            else if (File.Exists(AppPaths.BackendConfigPath)) LocalModeBox.IsChecked = true;
            else RemoteModeBox.IsChecked = true;
        }
        catch { RemoteModeBox.IsChecked = true; ErrorText.Text = "原连接配置无法读取，请重新填写管理员提供的信息。"; }
        UpdateIpState();
        Closed += (_, _) => _probe?.Cancel();
    }

    private void Mode_Checked(object sender, RoutedEventArgs e) => UpdateIpState();
    private void UpdateIpState()
    {
        if (ConnectionFields is not null) ConnectionFields.IsEnabled = RemoteModeBox.IsChecked == true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private ServiceConnectionSetup ReadConnection() => ServiceConnectionSetup.Parse(IpBox.Text, TokenBox.Password,
        _confirmedPin is null ? "" : Convert.ToBase64String(_confirmedPin));

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        _verified = null;
        _confirmedPin = null;
        VerifyButton.IsEnabled = SaveButton.IsEnabled = false;
        ErrorText.Text = "正在验证连接和证书…";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _probe = timeout;
        try
        {
            var address = MediaAgentSettings.NormalizeServiceUrl(IpBox.Text);
            // No HTTP or credentials are sent until the user confirms this certificate.
            var pin = await MediaAgentSettings.DiscoverSpkiAsync(address, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            PinBox.Text = Convert.ToHexString(SHA256.HashData(pin));
            timeout.CancelAfter(Timeout.InfiniteTimeSpan); // Human verification is not a network timeout.
            if (MessageBox.Show(this, $"服务：{address}\n证书 SHA-256：\n{PinBox.Text}\n\n请与管理员通过可信渠道核对。确认后才发送访问令牌。", "确认服务证书", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            { ErrorText.Text = "未确认服务证书，未发送令牌或保存配置。"; return; }
            timeout.Token.ThrowIfCancellationRequested();
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            _confirmedPin = pin;
            IpBox.Text = address;
            var connection = ReadConnection();
            using var handler = new HttpClientHandler { AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback = (_, certificate, _, _) => MediaAgentSettings.MatchesCertificate(certificate, pin) };
            using var client = new HttpClient(handler);
            using var gateway = new HttpIndexGateway(connection.BaseUrl, connection.Token, connection.SpkiPin, client);
            var health = await gateway.GetHealthAsync(timeout.Token);
            // /health is public; an authenticated read must succeed before saving a token.
            await gateway.GetImageAsync(0, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            _verified = connection;
            ErrorText.Text = $"验证成功：schema {health.SchemaVersion}。点击保存并重启后使用远程图库。";
        }
        catch (ArgumentException error) { ErrorText.Text = error.Message; }
        catch (OperationCanceledException) { ErrorText.Text = "验证已取消或超时，配置未保存。"; }
        catch { ErrorText.Text = "连接验证失败，请核对服务地址、令牌、证书指纹和服务器状态。配置未保存。"; }
        finally { _probe = null; VerifyButton.IsEnabled = SaveButton.IsEnabled = true; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位当前程序。");
            if (RemoteModeBox.IsChecked == true)
            {
                var connection = ReadConnection();
                if (_verified is null || connection.BaseUrl != _verified.BaseUrl || connection.Token != _verified.Token ||
                    !connection.SpkiPin.SequenceEqual(_verified.SpkiPin))
                { ErrorText.Text = "请先验证当前填写的连接信息；修改后需要重新验证。"; return; }
                connection.Save(AppPaths.BackendConfigPath, AppPaths.ServiceEndpointPath);
            }
            else AppPaths.SaveIndexBackendLocal();
            Process.Start(new ProcessStartInfo(processPath, $"--wait-for-pid {Environment.ProcessId}") { UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (ArgumentException error) { ErrorText.Text = error.Message; }
        catch { ErrorText.Text = "保存或重启失败，请检查配置目录权限后重试。"; }
    }
}
