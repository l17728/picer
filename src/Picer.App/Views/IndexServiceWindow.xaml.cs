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
            ? "客户端零构建逻辑：扫描、哈希、缩略图与识别均由索引服务完成（详见 docs/INDEX_SERVICE_CS_DESIGN_2026-09-08.md）。"
            : "当前为本机模式：索引在 Picer 进程内构建，未启用索引服务。";
        HealthText.Text = "尚未测试连接。";
    }

    private async void Probe_Click(object sender, RoutedEventArgs e)
    {
        ProbeButton.IsEnabled = false;
        HealthText.Text = "正在连接……";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var health = await AppServices.Gateway.GetHealthAsync(timeout.Token);
            stopwatch.Stop();
            HealthText.Text = $"连接正常：{health.Status}，schema v{health.SchemaVersion}，服务版本 {health.ServiceVersion}，往返 {stopwatch.ElapsedMilliseconds} ms。";
        }
        catch (Exception exception)
        {
            HealthText.Text = $"连接失败：{exception.Message}\n若为 service 模式，请确认索引服务已启动（启动后 %LOCALAPPDATA%\\Picer\\bootstrap\\service-endpoint.json 会更新）。";
        }
        finally
        {
            ProbeButton.IsEnabled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
