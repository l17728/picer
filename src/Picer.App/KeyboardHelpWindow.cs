using System.Windows;
using System.Windows.Controls;

namespace Picer.App;

internal sealed class KeyboardHelpWindow : Window
{
    public KeyboardHelpWindow()
    {
        Title = "键盘操作帮助";
        Width = 520;
        Height = 420;
        MinWidth = 320;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(16) };
        var close = new Button { Content = "关闭(_C)", IsCancel = true, MinWidth = 80,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom);
        panel.Children.Add(close);
        var text = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0),
            Text = "Ctrl+F：定位搜索框并全选\nCtrl+L：定位目录树\nCtrl+1～5：切换本地的目录、搜索、人物、标签与相册、视频页\nF1：打开本帮助\n\nTab / Shift+Tab：移动焦点\n方向键：选择列表、树或标签页\n空格：切换复选框或按下按钮\nShift+F10：打开所选项的菜单\n\n搜索框内 Esc：清空搜索\n本地搜索框内 Enter：执行地域搜索\n远程搜索框内 Enter：提交搜索\n远程图片网格内 Enter：打开所选原文件\n\n人物人脸列表内 Delete：标记为不分类，不删除原图。原文件删除仍需在删除流程中确认。" };
        System.Windows.Automation.AutomationProperties.SetName(text, "快捷键说明");
        panel.Children.Add(text);
        Content = panel;
    }
}
