using System.IO;
using System.Windows;

namespace Picer.App.Views;

public partial class RuntimeComponentsWindow : Window
{
    private readonly RuntimeComponentService _service = new(AppPaths.RuntimeRoot);

    public RuntimeComponentsWindow()
    {
        InitializeComponent();
        Components.ItemsSource = RuntimeComponentService.RequiredComponents.Select(spec =>
            new RuntimeComponentStatus(spec, File.Exists(_service.Resolve(spec)), false, false,
                File.Exists(_service.Resolve(spec)) ? "待校验" : "缺失")).ToArray();
        Details.Text = $"运行组件目录：{_service.RuntimeRoot}";
    }

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            var progress = new Progress<(int Completed, int Total, string Name)>(value =>
            {
                Progress.Value = value.Completed * 100d / value.Total; Details.Text = $"正在校验：{value.Name}";
            });
            var result = await _service.VerifyAsync(progress);
            Components.ItemsSource = result;
            Details.Text = result.All(value => value.Ready) ? "全部离线组件完整，可以自包含运行。" : "发布组件不完整或已损坏，请重新安装完整发布包。";
        }
        catch (Exception exception) { Details.Text = $"校验失败：{exception.Message}"; }
        finally { SetBusy(false); }
    }

    private async void SelfTest_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try { Details.Text = string.Join(Environment.NewLine, await _service.SelfTestAsync()); }
        catch (Exception exception) { Details.Text = $"自检失败：{exception.Message}"; }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy) { Components.IsEnabled = !busy; }
}
