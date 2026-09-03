using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Picer.App.Controls;

public sealed class MarkdownView : FlowDocumentScrollViewer
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(nameof(Markdown),
        typeof(string), typeof(MarkdownView), new PropertyMetadata("", (_, args) =>
        {
            if (_ is MarkdownView view) view.Render(args.NewValue as string ?? "");
        }));

    public string Markdown { get => (string)GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }

    public MarkdownView()
    {
        IsToolBarVisible = false; VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Document = new FlowDocument { PagePadding = new Thickness(0), FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 13 };
    }

    private void Render(string markdown)
    {
        var document = new FlowDocument { PagePadding = new Thickness(0), FontFamily = FontFamily, FontSize = FontSize };
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var paragraph = new Paragraph { Margin = new Thickness(0, 2, 0, 4) };
            if (line.StartsWith("### ")) { paragraph.FontSize = 14; paragraph.FontWeight = FontWeights.SemiBold; line = line[4..]; }
            else if (line.StartsWith("## ")) { paragraph.FontSize = 16; paragraph.FontWeight = FontWeights.SemiBold; line = line[3..]; }
            else if (line.StartsWith("# ")) { paragraph.FontSize = 18; paragraph.FontWeight = FontWeights.Bold; line = line[2..]; }
            else if (line.StartsWith("- ")) { paragraph.Inlines.Add(new Run("• ") { FontWeight = FontWeights.Bold }); line = line[2..]; }
            AddInlineMarkdown(paragraph, line);
            document.Blocks.Add(paragraph);
        }
        Document = document;
    }

    private static void AddInlineMarkdown(Paragraph paragraph, string value)
    {
        var position = 0;
        while (position < value.Length)
        {
            var open = value.IndexOf("**", position, StringComparison.Ordinal);
            if (open < 0) { paragraph.Inlines.Add(new Run(value[position..])); break; }
            if (open > position) paragraph.Inlines.Add(new Run(value[position..open]));
            var close = value.IndexOf("**", open + 2, StringComparison.Ordinal);
            if (close < 0) { paragraph.Inlines.Add(new Run(value[open..])); break; }
            paragraph.Inlines.Add(new Run(value[(open + 2)..close]) { FontWeight = FontWeights.Bold });
            position = close + 2;
        }
    }
}
