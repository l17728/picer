using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Windows;
using Microsoft.Win32;
using Picer.Core.Gateway;
using Picer.Core.Identity;

namespace Picer.App.Views;

public partial class AccountConnectionWindow : Window
{
    private readonly WindowsDeviceCredentialStore _store;
    private readonly CancellationTokenSource _lifetime = new();
    private DeviceAccountClient? _client;
    private readonly bool _forUpload;
    private readonly bool _asSource;
    public DeviceConnection? Result { get; private set; }

    public AccountConnectionWindow(string credentialPath, bool forUpload, bool asSource = false)
    {
        InitializeComponent();
        _forUpload = forUpload;
        _asSource = asSource;
        _store = new(credentialPath);
        PurposeText.Text = forUpload
            ? "先在本机建立索引，再选择上传到自己的空间。请在授权网页勾选“允许上传和修改索引”。其他电脑登录同一账号后，可以统一查看这些来源。"
            : "使用已有账号访问照片和视频索引，或创建自己的账号与空间。无需手工复制访问令牌。";
        if (asSource) PurposeText.Text = "为本机驻留程序创建独立的设备授权。请登录上传索引时使用的账号和空间；只读取你随后选择的来源目录。";
        Closed += (_, _) => { _lifetime.Cancel(); _client?.Dispose(); _lifetime.Dispose(); };
        if (_store.Exists)
        {
            try
            {
                _client = _store.OpenClient();
                if (_client.Connection.ClientKind != (asSource ? "agent" : "gui"))
                    throw new InvalidDataException("device_kind_mismatch");
                AddressBox.Text = _client.Connection.Origin;
                ShowFingerprint();
                if (_client.Connection.Session is not null)
                    StatusText.Text = "已保存设备连接。请导入新的认证文件，或重新验证已有连接。";
            }
            catch
            {
                _client?.Dispose(); _client = null;
                StatusText.Text = "已有设备凭据无法读取，请忘记此连接后重新配对；不会改动本地索引。";
            }
        }
    }

    private void ShowFingerprint()
    {
        if (_client is null) return;
        using var key = WindowsDeviceCredentialStore.OpenKey(_client.Connection);
        DeviceFingerprint.Text = DeviceAuthorization.KeyThumbprint(key);
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        LoginButton.IsEnabled = ImportButton.IsEnabled = ResetButton.IsEnabled = false;
        try
        {
            var origin = DeviceAuthorization.NormalizeOrigin(AddressBox.Text.Trim());
            if (_client is null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                StatusText.Text = "正在获取服务证书…";
                var pin = await MediaAgentSettings.DiscoverSpkiAsync(origin, timeout.Token);
                timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                var fingerprint = Convert.ToHexString(SHA256.HashData(pin));
                if (MessageBox.Show(this, $"服务：{origin}\n证书 SHA-256：\n{fingerprint}\n\n请通过可信渠道与管理员核对地址和证书指纹。确认后将打开此服务的登录页面。",
                        "核对服务身份", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                { StatusText.Text = "未确认服务身份。"; return; }
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var http = new HttpClient(DeviceAccountClient.CreateTransport(Convert.ToBase64String(pin))) { Timeout = TimeSpan.FromSeconds(20) };
                var discovery = await http.GetFromJsonAsync<ServiceDiscovery>(origin + "/identity/service", timeout.Token)
                    ?? throw new InvalidDataException("service_identity_missing");
                if (DeviceAuthorization.NormalizeOrigin(discovery.Origin) != origin || discovery.SpkiPin != Convert.ToBase64String(pin))
                    throw new InvalidDataException("service_identity_mismatch");
                _store.Create(discovery, Convert.ToBase64String(pin), Environment.MachineName + (_asSource ? " 驻留程序" : ""), _asSource ? "agent" : "gui");
                _client = _store.OpenClient();
            }
            if (_client.Connection.Origin != origin) throw new InvalidOperationException("请使用当前设备连接的服务地址。");
            ShowFingerprint();
            if (_client.Connection.Session is not null)
            {
                await VerifyAsync();
                return;
            }
            var enrollment = await _client.BeginEnrollmentAsync(_lifetime.Token);
            var expected = origin + "/account/approve?request=" + _client.Connection.RequestId;
            if (enrollment.ApprovalUrl != expected) throw new InvalidDataException("unexpected_approval_address");
            Process.Start(new ProcessStartInfo(expected) { UseShellExecute = true });
            StatusText.Text = "已打开浏览器。登录或注册后，下载认证文件并在此导入。";
        }
        catch (OperationCanceledException) { if (!_lifetime.IsCancellationRequested) StatusText.Text = "连接超时，请稍后重试。"; }
        catch { StatusText.Text = "无法连接照片空间。请核对服务地址、证书和网络；已有索引保持不变。"; }
        finally { if (!_lifetime.IsCancellationRequested) LoginButton.IsEnabled = ImportButton.IsEnabled = ResetButton.IsEnabled = true; }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null) { StatusText.Text = "请先输入服务地址并生成当前设备的配对请求。"; return; }
        var dialog = new OpenFileDialog { Title = "选择设备认证文件", Filter = "Picer 设备认证|*.picer-auth", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        ImportButton.IsEnabled = LoginButton.IsEnabled = ResetButton.IsEnabled = false;
        try
        {
            if (new FileInfo(dialog.FileName).Length > DeviceAuthorization.MaximumFileBytes) throw new InvalidDataException();
            await _client.ImportAsync(await File.ReadAllBytesAsync(dialog.FileName, _lifetime.Token), _lifetime.Token);
            await VerifyAsync();
        }
        catch (OperationCanceledException) { if (!_lifetime.IsCancellationRequested) StatusText.Text = "连接已取消，请稍后重试。"; }
        catch { StatusText.Text = "认证文件无效、已过期或不属于当前设备。请核对配对请求后重试。"; }
        finally { if (!_lifetime.IsCancellationRequested) ImportButton.IsEnabled = LoginButton.IsEnabled = ResetButton.IsEnabled = true; }
    }

    private async Task VerifyAsync()
    {
        if (_asSource)
        {
            using var socket = await _client!.ConnectWebSocketAsync("api/agent/v1/connect", _lifetime.Token);
            socket.Abort();
        }
        else
        {
            using var response = await _client!.SpaceClient.GetAsync("api/gateway/v1/images?pageSize=1", _lifetime.Token);
            response.EnsureSuccessStatusCode();
        }
        if (_forUpload && !_client.Connection.Session!.Scope.Split(' ').Contains("index.write"))
        {
            StatusText.Text = "当前设备只有查看权限。请忘记此连接后重新配对，并在网页勾选允许上传和修改索引。";
            Result = null;
            FinishButton.IsEnabled = false;
            return;
        }
        Result = _client.Connection;
        FinishButton.IsEnabled = true;
        StatusText.Text = $"已连接账号 {Result.Session!.AccountName} 的“{Result.Session.SpaceName}”。原始照片和视频仍保留在各自来源电脑。";
    }

    private void ServiceFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择服务地址文件", Filter = "Picer 服务地址|*.picer-service", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > DeviceAuthorization.MaximumFileBytes) throw new InvalidDataException();
            var document = DeviceAuthorization.ReadDocument<System.Text.Json.JsonElement>(File.ReadAllBytes(dialog.FileName));
            AddressBox.Text = DeviceAuthorization.NormalizeOrigin(document.GetProperty("origin").GetString()!);
            StatusText.Text = "已填写服务地址。登录前仍需核对服务证书并完成账号授权。";
        }
        catch { StatusText.Text = "服务地址文件无效。"; }
    }
    private void Finish_Click(object sender, RoutedEventArgs e) { if (Result?.Session is not null) DialogResult = true; }
    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        ResetButton.IsEnabled = LoginButton.IsEnabled = ImportButton.IsEnabled = false;
        try
        {
            _client?.Dispose(); _client = null;
            Result = null; FinishButton.IsEnabled = false;
            await _store.ForgetAsync(_lifetime.Token);
            DeviceFingerprint.Clear();
            StatusText.Text = "已移除本机连接，可以重新配对。旧设备授权可在原服务的账户网页撤销；本地索引不受影响。";
        }
        catch (OperationCanceledException) { }
        catch { StatusText.Text = "连接正在使用或无法移除，请关闭其他连接窗口后重试。"; }
        finally { if (!_lifetime.IsCancellationRequested) ResetButton.IsEnabled = LoginButton.IsEnabled = ImportButton.IsEnabled = true; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
