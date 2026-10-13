using System.Security.Cryptography;
using System.Windows.Forms;
using Picer.Core.Gateway;
using Picer.Core.Identity;

namespace Picer.MediaAgent;

internal sealed class AgentSettingsForm : Form
{
    private readonly TextBox _address = new() { Dock = DockStyle.Fill, PlaceholderText = "https://服务器:8445" };
    private readonly TextBox _token = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly ListBox _roots = new() { Dock = DockStyle.Fill };
    private readonly Button _save = new() { Text = "配对并保存", AutoSize = true };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly CancellationTokenSource _closed = new();
    public MediaAgentSettings? Settings { get; private set; }
    private DeviceConnection? _account;

    public AgentSettingsForm(MediaAgentSettings? current, Action<MediaAgentSettings>? save = null)
    {
        Text = "Picer 媒体代理设置";
        Width = 690; Height = 480; StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 8 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "索引服务地址", AutoSize = true }); layout.Controls.Add(_address);
        layout.Controls.Add(new Label { Text = "旧版访问令牌（使用账号登录时无需填写）", AutoSize = true }); layout.Controls.Add(_token);
        layout.Controls.Add(new Label { Text = "允许服务读取的媒体目录及其所有子目录", AutoSize = true }); layout.Controls.Add(_roots);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var add = new Button { Text = "添加目录", AutoSize = true };
        add.Click += (_, _) => { using var picker = new FolderBrowserDialog(); if (picker.ShowDialog(this) == DialogResult.OK && !_roots.Items.Contains(picker.SelectedPath)) _roots.Items.Add(picker.SelectedPath); };
        var remove = new Button { Text = "移除目录", AutoSize = true };
        remove.Click += (_, _) => { if (_roots.SelectedIndex >= 0) _roots.Items.RemoveAt(_roots.SelectedIndex); };
        var accountPath = current?.DeviceCredentialPath ?? AgentConfiguration.DeviceCredentialPath;
        var login = new Button { Text = "网页登录 / 导入认证", AutoSize = true };
        login.Click += (_, _) =>
        {
            using var form = new AgentAccountForm(accountPath, _address.Text);
            if (form.ShowDialog(this) != DialogResult.OK || form.Connection is not { } connection) return;
            _account = connection; _address.Text = connection.Origin; _address.ReadOnly = true; _token.Clear(); _token.Enabled = false;
            _status.Text = $"已连接 {connection.Session!.AccountName} / {connection.Session.SpaceName}，请选择允许读取的目录并保存。";
        };
        actions.Controls.AddRange([add, remove, login, _save]); layout.Controls.Add(actions); layout.Controls.Add(_status);
        Controls.Add(layout);
        if (current is not null) { _address.Text = current.ServiceUrl; _token.Text = current.Token; _roots.Items.AddRange(current.Roots); }
        if (current?.DeviceCredentialPath is not null)
        {
            try { _account = new WindowsDeviceCredentialStore(accountPath).Load(); _address.ReadOnly = true; _token.Enabled = false; }
            catch { _status.Text = "已保存的设备连接无法读取，请重新登录。"; }
        }
        _save.Click += async (_, _) =>
        {
            _save.Enabled = false;
            try
            {
                var address = MediaAgentSettings.NormalizeServiceUrl(_address.Text);
                if (_account is null && string.IsNullOrWhiteSpace(_token.Text) || _roots.Items.Count == 0) throw new ArgumentException("请登录账号或填写旧版令牌，并添加至少一个媒体目录。");
                if (_roots.Items.Cast<string>().Any(root => !Directory.Exists(root))) throw new ArgumentException("所选目录不存在，请重新选择。");
                if (_account is not null)
                {
                    using var account = new WindowsDeviceCredentialStore(accountPath).OpenClient();
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
                    using var socket = await account.ConnectWebSocketAsync("api/agent/v1/connect", deadline.Token); socket.Abort();
                    var accountSettings = (new MediaAgentSettings(account.Connection.Origin, "", account.Connection.SpkiPin, _roots.Items.Cast<string>().ToArray())
                        { DeviceCredentialPath = accountPath }).Validate();
                    if (save is null) AgentConfiguration.Save(accountSettings); else save(accountSettings);
                    Settings = accountSettings; DialogResult = DialogResult.OK; Close(); return;
                }
                _status.Text = "正在获取服务证书，尚未发送令牌……";
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var pin = await MediaAgentSettings.DiscoverSpkiAsync(address, timeout.Token);
                _closed.Token.ThrowIfCancellationRequested();
                var fingerprint = Convert.ToHexString(SHA256.HashData(pin));
                if (MessageBox.Show(this, "请与服务管理员核对公钥指纹：\n\n" + fingerprint + "\n\n确认一致后才会发送访问令牌。是否信任？", "确认服务证书", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                var settings = new MediaAgentSettings(address, _token.Text.Trim(), Convert.ToBase64String(pin), _roots.Items.Cast<string>().ToArray()).Validate();
                using var handler = new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false, ServerCertificateCustomValidationCallback = (_, cert, _, _) => MediaAgentSettings.MatchesCertificate(cert, pin) };
                using var client = new System.Net.Http.HttpClient(handler);
                using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, address + "/api/service/v1/capabilities");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.Token);
                using var authenticationTimeout = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
                authenticationTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var response = await client.SendAsync(request, authenticationTimeout.Token);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("服务拒绝连接，请检查令牌和服务地址。");
                if (save is null) AgentConfiguration.Save(settings); else save(settings);
                Settings = settings; DialogResult = DialogResult.OK; Close();
            }
            catch (OperationCanceledException) { if (!IsDisposed) _status.Text = "连接已取消或超时，请重试。"; }
            catch (Exception exception)
            { if (!IsDisposed) _status.Text = exception is ArgumentException ? exception.Message : "连接或保存失败，请检查服务地址、证书、令牌及用户配置权限。"; }
            finally { if (!IsDisposed) _save.Enabled = true; }
        };
        FormClosed += (_, _) => _closed.Cancel();
    }
}

internal static class AgentConfiguration
{
    public static string DeviceCredentialPath => Path.Combine(Environment.GetEnvironmentVariable("PICER_BOOTSTRAP_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Picer", "bootstrap"), "agent-device.bin");
    private static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Picer", "media-agent", "settings.json");
    public static MediaAgentSettings? Load() => MediaAgentSettings.Load(PathName, bytes => ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
    public static void Save(MediaAgentSettings settings) => settings.Save(PathName, bytes => ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
}
