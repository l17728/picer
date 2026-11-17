using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Picer.Core.Gateway;
using Picer.Core.Identity;

namespace Picer.MediaAgent;

internal sealed class AgentAccountForm : Form
{
    private readonly TextBox _address = new() { Dock = DockStyle.Top, PlaceholderText = "https://鏈嶅姟鍣?8445" };
    private readonly TextBox _fingerprint = new() { Dock = DockStyle.Top, ReadOnly = true, Multiline = true, Height = 55 };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = false };
    private readonly Button _login = new() { Text = "娴忚鍣ㄦ敞鍐屾垨鐧诲綍", AutoSize = true };
    private readonly Button _import = new() { Text = "瀵煎叆璁よ瘉鏂囦欢", AutoSize = true };
    private readonly Button _finish = new() { Text = "Verify and use", AutoSize = true };
    private readonly Button _reset = new() { Text = "Forget and re-pair", AutoSize = true };
    private readonly CancellationTokenSource _closed = new();
    private readonly WindowsDeviceCredentialStore _store;
    private DeviceAccountClient? _client;
    public DeviceConnection? Connection { get; private set; }

    public AgentAccountForm(string credentialPath, string? origin)
    {
        Text = "椹荤暀绋嬪簭鐧诲綍鐓х墖绌洪棿"; Width = 660; Height = 370; StartPosition = FormStartPosition.CenterParent;
        _store = new(credentialPath);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 6 };
        layout.Controls.Add(new Label { Text = "HTTPS service address", AutoSize = true });
        layout.Controls.Add(_address); layout.Controls.Add(new Label { Text = "缃戦〉鎺堟潈鏃惰鏍稿鏈満鎸囩汗", AutoSize = true });
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
                _status.Text = "Saved device connection; verify or re-pair.";
            }
        }
        catch { _client?.Dispose(); _client = null; _status.Text = "Saved credential is invalid; forget and re-pair."; }
        _login.Click += async (_, _) => await RunAsync(LoginAsync);
        _import.Click += async (_, _) => await RunAsync(ImportAsync);
        _finish.Click += async (_, _) => await RunAsync(VerifyAsync);
        _reset.Click += async (_, _) => await RunAsync(async () =>
        {
            _client?.Dispose(); _client = null; Connection = null;
            await _store.ForgetAsync(_closed.Token); _fingerprint.Clear();
            _status.Text = "Local connection removed.";
        });
        FormClosed += (_, _) => { _closed.Cancel(); _client?.Dispose(); };
    }

    private async Task RunAsync(Func<Task> action)
    {
        _login.Enabled = _import.Enabled = _finish.Enabled = _reset.Enabled = false;
        try { await action(); }
        catch (OperationCanceledException) { if (!IsDisposed) _status.Text = "Operation cancelled or timed out."; }
        catch { if (!IsDisposed) _status.Text = "Connection or pairing failed."; }
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
            if (MessageBox.Show(this, $"Service: {origin}\\nCertificate fingerprint: {Convert.ToHexString(SHA256.HashData(pin))}\\nTrust this service?", "Confirm service", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            using var http = new HttpClient(DeviceAccountClient.CreateTransport(Convert.ToBase64String(pin)));
            var discovery = await http.GetFromJsonAsync<ServiceDiscovery>(origin + "/identity/service", deadline.Token) ?? throw new InvalidDataException();
            if (discovery.Origin != origin || discovery.SpkiPin != Convert.ToBase64String(pin)) throw new InvalidDataException();
            _store.Create(discovery, discovery.SpkiPin, Environment.MachineName + " 椹荤暀绋嬪簭", "agent");
            _client = _store.OpenClient();
        }
        if (_client.Connection.Origin != origin) throw new InvalidOperationException();
        ShowFingerprint();
        if (_client.Connection.Session is not null) { await VerifyAsync(); return; }
        var enrollment = await _client.BeginEnrollmentAsync(_closed.Token);
        var expected = origin + "/account/approve?request=" + _client.Connection.RequestId;
        if (enrollment.ApprovalUrl != expected) throw new InvalidDataException();
        Process.Start(new ProcessStartInfo(expected) { UseShellExecute = true });
        _status.Text = "Complete login and approve the device, then import the credential file.";
    }

    private async Task ImportAsync()
    {
        if (_client is null) { _status.Text = "Click browser login first."; return; }
        using var picker = new OpenFileDialog { Filter = "Picer 璁惧璁よ瘉|*.picer-auth", CheckFileExists = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        if (new FileInfo(picker.FileName).Length > DeviceAuthorization.MaximumFileBytes) throw new InvalidDataException();
        await _client.ImportAsync(await File.ReadAllBytesAsync(picker.FileName, _closed.Token), _closed.Token);
        await VerifyAsync();
    }

    private async Task VerifyAsync()
    {
        if (_client?.Connection.Session is null) { _status.Text = "Complete device pairing first."; return; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var socket = await _client.ConnectWebSocketAsync("api/agent/v1/connect", deadline.Token); socket.Abort();
        Connection = _client.Connection; DialogResult = DialogResult.OK; Close();
    }
}

