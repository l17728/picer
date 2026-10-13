using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Picer.Core.Gateway;
using Picer.Core.Identity;

namespace Picer.MediaAgent;

internal sealed class AgentAccountForm : Form
{
    private readonly TextBox _address = new() { Dock = DockStyle.Top, PlaceholderText = "https://服务器:8445" };
    private readonly TextBox _fingerprint = new() { Dock = DockStyle.Top, ReadOnly = true, Multiline = true, Height = 55 };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = false };
    private readonly Button _login = new() { Text = "浏览器注册或登录", AutoSize = true };
    private readonly Button _import = new() { Text = "导入认证文件", AutoSize = true };
    private readonly Button _finish = new() { Text = "验证并使用", AutoSize = true };
    private readonly Button _reset = new() { Text = "忘记并重新配对", AutoSize = true };
    private readonly CancellationTokenSource _closed = new();
    private readonly WindowsDeviceCredentialStore _store;
    private DeviceAccountClient? _client;
    public DeviceConnection? Connection { get; private set; }

    public AgentAccountForm(string credentialPath, string? origin)
    {
        Text = "驻留程序登录照片空间"; Width = 660; Height = 370; StartPosition = FormStartPosition.CenterParent;
        _store = new(credentialPath);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 6 };
        layout.Controls.Add(new Label { Text = "服务 HTTPS 地址：驻留程序使用独立设备授权", AutoSize = true });
        layout.Controls.Add(_address); layout.Controls.Add(new Label { Text = "网页授权时请核对本机指纹", AutoSize = true });
        layout.Controls.Add(_fingerprint);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        actions.Controls.AddRange([_login, _import, _finish, _reset]); layout.Controls.Add(actions); layout.Controls.Add(_status);
        Controls.Add(layout); _address.Text = origin ?? "";
        try
        {
            if (_store.Exists)
            {
                _client = _store.OpenClient();
                if (_client.Connection.ClientKind != "agent") throw new InvalidDataException();
                _address.Text = _client.Connection.Origin; ShowFingerprint();
                _status.Text = "已保存设备连接，可以验证使用。来源目录由本机设置授权，空间来源由 GUI 上传时关联。";
            }
        }
        catch { _client?.Dispose(); _client = null; _status.Text = "已有凭据不可用，请忘记连接后重新配对。"; }
        _login.Click += async (_, _) => await RunAsync(LoginAsync);
        _import.Click += async (_, _) => await RunAsync(ImportAsync);
        _finish.Click += async (_, _) => await RunAsync(VerifyAsync);
        _reset.Click += async (_, _) => await RunAsync(async () =>
        {
            _client?.Dispose(); _client = null; Connection = null;
            await _store.ForgetAsync(_closed.Token); _fingerprint.Clear();
            _status.Text = "本机连接已移除。旧授权可在原服务账户网页撤销。";
        });
        FormClosed += (_, _) => { _closed.Cancel(); _client?.Dispose(); };
    }

    private async Task RunAsync(Func<Task> action)
    {
        _login.Enabled = _import.Enabled = _finish.Enabled = _reset.Enabled = false;
        try { await action(); }
        catch (OperationCanceledException) { if (!IsDisposed) _status.Text = "操作已取消或超时。"; }
        catch { if (!IsDisposed) _status.Text = "连接或配对失败，请检查地址、证书、认证文件和网络。现有来源文件不受影响。"; }
        finally { if (!IsDisposed) _login.Enabled = _import.Enabled = _finish.Enabled = _reset.Enabled = true; }
    }

    private void ShowFingerprint()
    {
        using var key = WindowsDeviceCredentialStore.OpenKey(_client!.Connection);
        _fingerprint.Text = DeviceAuthorization.KeyThumbprint(key);
    }

    private async Task LoginAsync()
    {
        var origin = DeviceAuthorization.NormalizeOrigin(_address.Text.Trim());
        if (_client is null)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            var pin = await MediaAgentSettings.DiscoverSpkiAsync(origin, deadline.Token);
            deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            if (MessageBox.Show(this, $"服务：{origin}\n证书 SHA-256：{Convert.ToHexString(SHA256.HashData(pin))}\n请通过可信渠道核对。是否信任？",
                    "确认服务身份", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            using var http = new HttpClient(DeviceAccountClient.CreateTransport(Convert.ToBase64String(pin)));
            var discovery = await http.GetFromJsonAsync<ServiceDiscovery>(origin + "/identity/service", deadline.Token) ?? throw new InvalidDataException();
            if (discovery.Origin != origin || discovery.SpkiPin != Convert.ToBase64String(pin)) throw new InvalidDataException();
            _store.Create(discovery, discovery.SpkiPin, Environment.MachineName + " 驻留程序", "agent");
            _client = _store.OpenClient();
        }
        if (_client.Connection.Origin != origin) throw new InvalidOperationException();
        ShowFingerprint();
        if (_client.Connection.Session is not null) { await VerifyAsync(); return; }
        var enrollment = await _client.BeginEnrollmentAsync(_closed.Token);
        var expected = origin + "/account/approve?request=" + _client.Connection.RequestId;
        if (enrollment.ApprovalUrl != expected) throw new InvalidDataException();
        Process.Start(new ProcessStartInfo(expected) { UseShellExecute = true });
        _status.Text = "请在浏览器登录或注册，选择空间、核对指纹并下载认证文件，然后在此导入。";
    }

    private async Task ImportAsync()
    {
        if (_client is null) { _status.Text = "请先点击浏览器注册或登录，建立本机配对请求。"; return; }
        using var picker = new OpenFileDialog { Filter = "Picer 设备认证|*.picer-auth", CheckFileExists = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        if (new FileInfo(picker.FileName).Length > DeviceAuthorization.MaximumFileBytes) throw new InvalidDataException();
        await _client.ImportAsync(await File.ReadAllBytesAsync(picker.FileName, _closed.Token), _closed.Token);
        await VerifyAsync();
    }

    private async Task VerifyAsync()
    {
        if (_client?.Connection.Session is null) { _status.Text = "请先完成设备配对。"; return; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var socket = await _client.ConnectWebSocketAsync("api/agent/v1/connect", deadline.Token); socket.Abort();
        Connection = _client.Connection; DialogResult = DialogResult.OK; Close();
    }
}
