using System.Windows;
using System.Windows.Input;
using Picer.App.Services;
using Picer.Core.Logging;
using Picer.Core.Search;

namespace Picer.App.Views;

public partial class VoiceAssistantWindow : Window
{
    private readonly VoiceCommandAgent _agent;
    private readonly Func<IReadOnlyList<string>> _locations;
    private readonly Func<IReadOnlyList<string>> _people;
    private readonly Func<VoiceAction, Task> _execute;
    private readonly FullDuplexVoiceSession _voice;
    private CancellationTokenSource? _turn;

    public VoiceAssistantWindow(VoiceCommandAgent agent, Func<IReadOnlyList<string>> locations, Func<IReadOnlyList<string>> people,
        Func<VoiceAction, Task> execute, Uri asrEndpoint, string asrModel, Uri ttsEndpoint)
    {
        InitializeComponent();
        _agent = agent; _locations = locations; _people = people; _execute = execute;
        _voice = new FullDuplexVoiceSession(asrEndpoint, asrModel, ttsEndpoint);
        _voice.StateChanged += value => Dispatcher.BeginInvoke(() => StatusText.Text = value);
        _voice.SpeechDetectedDuringPlayback += () => Dispatcher.BeginInvoke(() =>
        {
            // Stopping TTS alone leaves the agent request running. Cancel the
            // whole turn so an interrupted answer cannot later speak stale text.
            _turn?.Cancel();
            StatusText.Text = "已检测到插话，正在重新聆听";
        });
        _voice.UtteranceRecognized += text => Dispatcher.InvokeAsync(() => RunTurnAsync(text)).Task.Unwrap();
        Conversation.Items.Add("助手：可以说“打开目录浏览，看看日本的照片”。目标不明确时我会继续询问。");
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await RunTurnAsync(InputBox.Text);
    private async void InputBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return; e.Handled = true; await RunTurnAsync(InputBox.Text); }
    private void Listen_Checked(object sender, RoutedEventArgs e) { try { _voice.Start(); ListenButton.Content = "停止语音"; } catch (Exception exception) { ListenButton.IsChecked = false; StatusText.Text = $"无法启动麦克风：{exception.Message}"; } }
    private void Listen_Unchecked(object sender, RoutedEventArgs e) { _voice.Stop(); ListenButton.Content = "开始语音"; }

    private async Task RunTurnAsync(string input)
    {
        var text = input.Trim(); if (text.Length == 0) return;
        _turn?.Cancel(); _turn?.Dispose(); _turn = new CancellationTokenSource(); _voice.CancelPlayback();
        var token = _turn.Token; SendButton.IsEnabled = false; StopButton.IsEnabled = true;
        InputBox.Clear(); Conversation.Items.Add($"用户：{text}"); StatusText.Text = "正在理解指令…";
        try
        {
            var action = await _agent.PlanAsync(text, _locations(), _people(), token);
            Conversation.Items.Add($"助手：{action.Reply}"); Conversation.ScrollIntoView(Conversation.Items[^1]);
            await _execute(action); StatusText.Text = action.Kind == VoiceActionKind.Clarify ? "等待补充信息" : "已执行";
            if (_voice.IsListening) await _voice.SpeakAsync(action.Reply, token);
        }
        catch (OperationCanceledException) { StatusText.Text = "已打断"; }
        catch (Exception exception) { AppLogger.Error("语音代理处理失败", exception); StatusText.Text = "处理失败"; Conversation.Items.Add($"助手：处理失败：{exception.Message}"); }
        finally { SendButton.IsEnabled = true; StopButton.IsEnabled = false; }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) { _turn?.Cancel(); _voice.CancelPlayback(); }
    protected override void OnClosed(EventArgs e) { _turn?.Cancel(); _turn?.Dispose(); _voice.Dispose(); base.OnClosed(e); }
}
