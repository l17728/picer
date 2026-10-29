using System.Windows;
using System.Windows.Controls;

namespace Picer.App.Views;

/// <summary>Show committed local information before optional remote enrichment.</summary>
public sealed class AsyncDetailsWindow : Window
{
    public AsyncDetailsWindow(string title, string text, Func<CancellationToken, Task<string?>>? enrich = null)
    {
        Title = title; Width = 660; Height = 520; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var stop = new CancellationTokenSource();
        var token = stop.Token;
        var body = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16) };
        Content = body;
        Closed += (_, _) => { stop.Cancel(); stop.Dispose(); };
        Loaded += async (_, _) =>
        {
            try
            {
                if (enrich is null) return;
                var extra = await enrich(token);
                if (!token.IsCancellationRequested && !string.IsNullOrWhiteSpace(extra)) body.Text = text + "\n\n" + extra;
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (!token.IsCancellationRequested) body.Text = text + "\n\n详细地点暂不可用，已显示本地信息。";
            }
        };
    }
}
