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
        SyncButton.Visibility = AppServices.ServiceMode ? Visibility.Collapsed : Visibility.Visible;
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
            MediaAgentController.ResolveToken(),
            MediaAgentController.ResolveSpkiPin(), roots);
        if (!started) { AgentStatusText.Text = "找不到 Picer.MediaAgent.exe，请先完成发布或 Debug 构建。"; return; }
        MediaAgentController.Save(new MediaAgentController.Settings(AgentEnabledBox.IsChecked == true, ip));
        AgentStatusText.Text = "原图代理已启动，等待 IndexService 连接。";
        AgentButton.Content = "重新启动代理";
    }

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (AppServices.ServiceMode || AppServices.Db is null) { HealthText.Text = "当前不是本地索引，不能从此窗口推送本地索引。"; return; }
        var ip = AgentIpBox.Text.Trim();
        var token = MediaAgentController.ResolveToken();
        if (!System.Net.IPAddress.TryParse(ip, out _) || string.IsNullOrWhiteSpace(token))
        {
            HealthText.Text = "请先填写有效的 IndexService IP，并配置 PICER_INDEX_SERVICE_TOKEN。";
            return;
        }
        SyncButton.IsEnabled = false;
        try
        {
            // 同步前确保常驻代理已启动；这样服务端收到索引后即可立即回源读取原图/原视频。
            var settings = new MediaAgentController.Settings(AgentEnabledBox.IsChecked == true, ip);
            MediaAgentController.Save(settings);
            if (settings.Enabled)
            {
                var roots = AppServices.Db.GetSources().Select(source => source.Path);
                if (!MediaAgentController.Start(ip, token,
                        MediaAgentController.ResolveSpkiPin(), roots))
                {
                    AgentStatusText.Text = "无法启动原图代理，索引仍会继续同步；请检查 media-agent 发布文件。";
                }
                else
                {
                    AgentStatusText.Text = "原图代理已启动，正在同步索引。";
                }
            }
            var client = Picer.Core.Gateway.IndexSyncClient.Create($"https://{ip}:8445", token);
            var progress = new Progress<(int done, int total)>(value => HealthText.Text = $"正在同步索引：{value.done}/{value.total}…");
            var count = await client.PushImagesAsync(AppServices.Db, progress);
            var videoCount = await client.PushVideosAsync(AppServices.Db, progress);
            HealthText.Text = $"索引同步完成：{count} 张图片、{videoCount} 个视频记录已发送。代理可按需提供原图。";
        }
        catch (Exception exception) { HealthText.Text = $"索引同步失败：{exception.Message}"; }
        finally { SyncButton.IsEnabled = true; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
