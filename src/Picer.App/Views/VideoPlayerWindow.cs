using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Picer.App.Services;
using Picer.Core.Gateway;

namespace Picer.App.Views;

public sealed class VideoPlayerWindow : Window
{
    public VideoPlayerSettings? Selection { get; private set; }
    public VideoPlayerWindow()
    {
        Title = "视频播放器与读取方式"; Width = 540; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var current = VideoPlayerSettings.Load();
        var panel = new StackPanel { Margin = new Thickness(20) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "播放器（留空使用系统默认播放器）" });
        var path = new TextBox { Text = current.Executable, Margin = new Thickness(0, 6, 0, 6) }; panel.Children.Add(path);
        var choose = new Button { Content = "选择播放器程序…", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 10, 4) };
        choose.Click += (_, _) => { var dialog = new OpenFileDialog { Filter = "播放器程序 (*.exe)|*.exe", CheckFileExists = true }; if (dialog.ShowDialog(this) == true) path.Text = dialog.FileName; };
        panel.Children.Add(choose);
        var mode = new ComboBox { ItemsSource = new[] { "自动：识别播放器能力，不确定时下载", "优先流式：尝试所选播放器，失败时下载", "下载完成后播放" }, SelectedIndex = (int)current.Mode, Margin = new Thickness(0, 12, 0, 8) }; panel.Children.Add(mode);
        panel.Children.Add(new TextBlock { Text = "下载并发连接数（网络或源磁盘较慢时可保持 2）" });
        var connections = new ComboBox { ItemsSource = new[] { 2, 3, 4 }, SelectedItem = current.Connections, Margin = new Thickness(0, 4, 0, 8) }; panel.Children.Add(connections);
        var ask = new CheckBox { Content = "每次播放前选择", IsChecked = current.AskEveryTime }; panel.Children.Add(ask);
        panel.Children.Add(new TextBlock { Text = "已识别的 VLC / mpv 优先使用流式。读取成功不等于支持所有编码；播放窗口可随时切换到下载。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        var save = new Button { Content = "确定", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(20, 4, 20, 4) };
        save.Click += (_, _) =>
        {
            if (path.Text.Length > 0 && (!File.Exists(path.Text) || !Path.GetExtension(path.Text).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
            { MessageBox.Show(this, "请选择已安装的播放器 EXE。", Title); return; }
            Selection = new(path.Text.Trim(), (VideoPlaybackMode)mode.SelectedIndex, (int)connections.SelectedItem, ask.IsChecked == true);
            try { Selection.Save(); DialogResult = true; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { MessageBox.Show(this, "无法保存播放器设置，请检查数据目录是否可写。", Title); }
        };
        panel.Children.Add(save);
    }
}
