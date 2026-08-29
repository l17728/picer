using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Picer.Core.Storage;

namespace Picer.App;

public partial class StorageSettingsWindow : Window
{
    public StorageSettingsWindow()
    {
        InitializeComponent();
        var config = AppServices.Config;
        StorageType.SelectedIndex = string.Equals(config.Type, "webdav", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        ServerUrl.Text = config.WebDAV?.ServerUrl ?? "";
        BasePath.Text = config.WebDAV?.BasePath ?? "/";
        Username.Text = config.WebDAV?.Username ?? "";
        CacheEnabled.IsChecked = config.Cache?.Enabled ?? true;
        CacheDirectory.Text = config.Cache?.Directory ?? Path.Combine(AppPaths.DataRoot, "cache");
        CacheSizeGb.Text = (config.Cache?.MaxSizeGb ?? 10).ToString();
        RefreshPasswordStatus(); UpdateEnabledState();
    }

    public bool Applied { get; private set; }

    private bool IsWebDav => StorageType.SelectedItem is ComboBoxItem { Tag: "webdav" };

    private void StorageType_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateEnabledState();

    private void UpdateEnabledState()
    {
        if (ServerUrl is null) return;
        foreach (var control in new Control[] { ServerUrl, BasePath, Username, Password, TestButton }) control.IsEnabled = IsWebDav;
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (!TryValidate(out var error)) { Status.Text = error; return; }
        var password = Password.Password.Length > 0 ? Password.Password : AppServices.GetWebDavPassword() ?? "";
        TestButton.IsEnabled = false; Status.Text = "正在连接 WebDAV…";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            using var provider = new WebDAVStorageProvider(ServerUrl.Text.Trim(), Username.Text.Trim(), password, NormalizeBasePath());
            var info = await provider.GetInfoAsync(NormalizeBasePath(), timeout.Token);
            Status.Text = info is null ? "连接成功，但服务器未返回基础路径信息。请检查基础路径后保存。" : "连接与身份验证成功。";
        }
        catch (OperationCanceledException) { Status.Text = "连接测试超时（20 秒）。"; }
        catch (Exception exception) { Status.Text = $"连接失败：{exception.Message}"; }
        finally { TestButton.IsEnabled = true; }
    }

    private void ChooseCache_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 WebDAV 缓存目录", InitialDirectory = CacheDirectory.Text };
        if (dialog.ShowDialog(this) == true) CacheDirectory.Text = dialog.FolderName;
    }

    private void DeletePassword_Click(object sender, RoutedEventArgs e)
    {
        AppServices.DeleteWebDavPassword(); Password.Clear(); RefreshPasswordStatus(); Status.Text = "已从 Windows 凭据管理器删除 WebDAV 密码。";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryValidate(out var error)) { Status.Text = error; return; }
        var config = new StorageConfig
        {
            Type = IsWebDav ? "webdav" : "local",
            Local = new LocalConfig(),
            WebDAV = IsWebDav ? new WebDAVConfig { ServerUrl = ServerUrl.Text.Trim(), Username = Username.Text.Trim(), BasePath = NormalizeBasePath() } : null,
            Cache = new CacheConfig { Enabled = CacheEnabled.IsChecked == true, Directory = CacheDirectory.Text.Trim(), MaxSizeGb = int.Parse(CacheSizeGb.Text) }
        };
        try
        {
            AppServices.ApplyStorageConfig(config, Password.Password);
            Applied = true; DialogResult = true;
        }
        catch (Exception exception) { Status.Text = $"保存失败：{exception.Message}"; }
    }

    private bool TryValidate(out string error)
    {
        error = "";
        if (!int.TryParse(CacheSizeGb.Text, out var cacheSize) || cacheSize is < 1 or > 1000) { error = "缓存容量必须是 1–1000 GB 的整数。"; return false; }
        if (!IsWebDav) return true;
        if (!Uri.TryCreate(ServerUrl.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) { error = "请输入有效的 HTTP/HTTPS WebDAV 服务器地址。"; return false; }
        if (Username.Text.Trim().Length > 0 && Password.Password.Length == 0 && string.IsNullOrEmpty(AppServices.GetWebDavPassword())) { error = "请填写 WebDAV 密码。"; return false; }
        return true;
    }

    private string NormalizeBasePath() => "/" + BasePath.Text.Trim().Trim('/');
    private void RefreshPasswordStatus() => PasswordStatus.Text = AppServices.GetWebDavPassword() is { Length: > 0 } ? "已安全保存密码；留空继续使用。" : "尚未保存密码。";
}
