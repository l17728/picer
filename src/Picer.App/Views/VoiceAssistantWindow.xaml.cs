using System.Windows;
using System.Windows.Input;
using Picer.Core.Logging;
using Picer.Core.Search;

namespace Picer.App.Views;

public partial class VoiceAssistantWindow : Window
{
    private readonly VoiceCommandAgent _agent;
    private readonly Func<IReadOnlyList<string>> _locations;
    private readonly Func<IReadOnlyList<string>> _people;
    private readonly Func<VoiceAction, Task> _execute;
    private CancellationTokenSource? _turn;

    public VoiceAssistantWindow(VoiceCommandAgent agent, Func<IReadOnlyList<string>> locations,
        Func<IReadOnlyList<string>> people, Func<VoiceAction, Task> execute)
    {
        InitializeComponent();
        _agent = agent; _locations = locations; _people = people; _execute = execute;
        Conversation.Items.Add("助手：可以说或输入“打开目录浏览，看看日本的照片”。目标不明确时我会询问。");
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await RunTurnAsync();
    private async void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true; await RunTurnAsync();
    }

    private async Task RunTurnAsync()
    {
        var text = InputBox.Text.Trim(); if (text.Length == 0) return;
        _turn?.Cancel(); _turn?.Dispose(); _turn = new CancellationTokenSource();
        var token = _turn.Token; SendButton.IsEnabled = false; StopButton.IsEnabled = true;
        InputBox.Clear(); Conversation.Items.Add($"用户：{text}"); StatusText.Text = "正在理解指令…";
        try
        {
            var action = await _agent.PlanAsync(text, _locations(), _people(), token);
            Conversation.Items.Add($"助手：{action.Reply}");
            await _execute(action); StatusText.Text = action.Kind == VoiceActionKind.Clarify ? "等待补充信息" : "已执行";
        }
        catch (OperationCanceledException) { StatusText.Text = "已打断"; }
        catch (Exception exception) { AppLogger.Error("语音代理处理失败", exception); StatusText.Text = "处理失败"; Conversation.Items.Add($"助手：处理失败：{exception.Message}"); }
        finally { SendButton.IsEnabled = true; StopButton.IsEnabled = false; }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _turn?.Cancel();
    protected override void OnClosed(EventArgs e) { _turn?.Cancel(); _turn?.Dispose(); base.OnClosed(e); }
}
