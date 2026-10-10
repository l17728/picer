using System.Security.Cryptography;
using System.Windows.Forms;
using Picer.Core.Gateway;

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

    public AgentSettingsForm(MediaAgentSettings? current, Action<MediaAgentSettings>? save = null)
    {
        Text = "Picer 媒体代理设置";
        Width = 620; Height = 460; StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 8 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "索引服务地址", AutoSize = true }); layout.Controls.Add(_address);
        layout.Controls.Add(new Label { Text = "访问令牌（加密保存在当前 Windows 用户配置中）", AutoSize = true }); layout.Controls.Add(_token);
        layout.Controls.Add(new Label { Text = "允许服务读取的媒体目录及其所有子目录", AutoSize = true }); layout.Controls.Add(_roots);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var add = new Button { Text = "添加目录", AutoSize = true };
        add.Click += (_, _) => { using var picker = new FolderBrowserDialog(); if (picker.ShowDialog(this) == DialogResult.OK && !_roots.Items.Contains(picker.SelectedPath)) _roots.Items.Add(picker.SelectedPath); };
        var remove = new Button { Text = "移除目录", AutoSize = true };
        remove.Click += (_, _) => { if (_roots.SelectedIndex >= 0) _roots.Items.RemoveAt(_roots.SelectedIndex); };
        actions.Controls.AddRange([add, remove, _save]); layout.Controls.Add(actions); layout.Controls.Add(_status);
        Controls.Add(layout);
        if (current is not null) { _address.Text = current.ServiceUrl; _token.Text = current.Token; _roots.Items.AddRange(current.Roots); }
        _save.Click += async (_, _) =>
        {
            _save.Enabled = false;
            try
            {
                var address = MediaAgentSettings.NormalizeServiceUrl(_address.Text);
                if (string.IsNullOrWhiteSpace(_token.Text) || _roots.Items.Count == 0) throw new ArgumentException("请填写令牌并添加至少一个媒体目录。");
                if (_roots.Items.Cast<string>().Any(root => !Directory.Exists(root))) throw new ArgumentException("所选目录不存在，请重新选择。");
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
    private static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Picer", "media-agent", "settings.json");
    public static MediaAgentSettings? Load() => MediaAgentSettings.Load(PathName, bytes => ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
    public static void Save(MediaAgentSettings settings) => settings.Save(PathName, bytes => ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
}
