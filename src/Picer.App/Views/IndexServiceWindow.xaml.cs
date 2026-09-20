using System.Diagnostics;
using System.Windows;

namespace Picer.App.Views;

public partial class IndexServiceWindow : Window
{
    public IndexServiceWindow()
    {
        InitializeComponent();
        ModeText.Text = AppServices.ServiceMode ? "模式：service（连接索引服务）" : "模式：local（本机进程内索引）";
        TargetText.Text = AppServices.ServiceMode
            ? "客户端只读连接 IndexService；扫描、索引、缩略图和搜索由服务端完成。"
            : "当前为本地模式：索引在 Picer 进程内构建。";
        BrowseButton.Visibility = AppServices.ServiceMode ? Visibility.Visible : Visibility.Collapsed;
        AgentBox.Visibility = AppServices.ServiceMode ? Visibility.Collapsed : Visibility.Visible;
        var settings = MediaAgentController.Load();
        AgentEnabledBox.IsChecked = settings.Enabled;
        AgentIpBox.Text = settings.ServiceIp;
        AgentStatusText.Text = settings.Enabled ? "已配置为保持代理运行。" : "未启动本机原图代理。";
        HealthText.Text = "尚未测试连接。";
    }

    private void Browse_Click(object sender, RoutedEventArgs e) => new ServiceBrowseWindow { Owner = this }.Show();

    private async void Probe_Click(object sender, RoutedEventArgs e)
    {
        ProbeButton.IsEnabled = false;
        HealthText.Text = "正在连接…";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var health = await AppServices.Gateway.GetHealthAsync(timeout.Token);
            HealthText.Text = $"连接正常：{health.Status}；schema v{health.SchemaVersion}；服务版本 {health.ServiceVersion}；往返 {stopwatch.ElapsedMilliseconds} ms。";
        }
        catch (Exception exception) { HealthText.Text = $"连接失败：{exception.Message}"; }
        finally { ProbeButton.IsEnabled = true; }
    }

    private void Agent_Click(object sender, RoutedEventArgs e)
    {
        var ip = AgentIpBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(ip)) { AgentStatusText.Text = "请填写 IndexService IP。"; return; }
        if (!System.Net.IPAddress.TryParse(ip, out _)) { AgentStatusText.Text = "IP 地址格式不正确。"; return; }
        var roots = AppServices.Db?.GetSources().Select(source => source.Path) ?? [];
        var started = MediaAgentController.Start(ip,
            Environment.GetEnvironmentVariable("PICER_INDEX_SERVICE_TOKEN") ?? "",
            Environment.GetEnvironmentVariable("PICER_INDEX_SERVICE_SPKI_PIN"), roots);
        if (!started) { AgentStatusText.Text = "找不到 Picer.MediaAgent.exe，请先完成发布或 Debug 构建。"; return; }
        MediaAgentController.Save(new MediaAgentController.Settings(AgentEnabledBox.IsChecked == true, ip));
        AgentStatusText.Text = "原图代理已启动，等待 IndexService 连接。";
        AgentButton.Content = "重新启动代理";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
