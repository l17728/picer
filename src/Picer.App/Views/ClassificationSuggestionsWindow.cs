using System.Windows;
using System.Windows.Controls;
using Picer.App.Controls;
using Picer.App.ViewModels;
using Picer.Core.Classification;

namespace Picer.App.Views;

internal sealed class ClassificationSuggestionsWindow : Window
{
    private readonly ListBox _candidates = new() { Width = 180, DisplayMemberPath = "Draft.Name" };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _feedback = new();
    private readonly WrapPanel _examples = new();
    private readonly ComboBox _merge = new() { Width = 150, DisplayMemberPath = "Name" };
    private readonly CancellationTokenSource _closed = new();
    internal ClassificationJob? MatchJob { get; private set; }

    internal ClassificationSuggestionsWindow(ClassificationV2Client client, ClassificationTaxonomySnapshot taxonomy,
        IReadOnlyList<ClassificationProposal> proposals)
    {
        Title = "新标签建议"; Width = 820; Height = 520; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Closed += (_, _) => _closed.Cancel();
        var layout = new DockPanel { Margin = new Thickness(12) };
        var actions = new WrapPanel();
        DockPanel.SetDock(_feedback, Dock.Bottom); layout.Children.Add(_feedback);
        DockPanel.SetDock(actions, Dock.Bottom); layout.Children.Add(actions);
        DockPanel.SetDock(_candidates, Dock.Left); layout.Children.Add(_candidates);
        var detail = new DockPanel { Margin = new Thickness(10) };
        DockPanel.SetDock(_details, Dock.Top); detail.Children.Add(_details);
        detail.Children.Add(new ScrollViewer { Content = _examples, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        layout.Children.Add(detail); Content = layout;
        AddButton("接受并匹配", "accept"); AddButton("拒绝建议", "reject");
        _merge.ItemsSource = taxonomy.Labels.Where(l => l.Active && l.Assignable).OrderBy(l => l.Name).ToArray();
        actions.Children.Add(_merge); AddButton("合并到已有标签", "merge");
        var selectionVersion = 0;
        _candidates.SelectionChanged += async (_, _) =>
        {
            var version = ++selectionVersion;
            _examples.Children.Clear();
            if (_candidates.SelectedItem is not ClassificationProposal p) return;
            var parents = p.Draft.ParentIds.Select(id => taxonomy.Labels.FirstOrDefault(l => l.Id == id)?.Name ?? id);
            _details.Text = $"{p.Draft.Name}　上级分类：{string.Join("、", parents)}\n{p.Draft.Definition}\n排除条件：{p.Draft.Exclusions}\n代表照片（{p.ImageIds.Count}）";
            try
            {
                foreach (var id in p.ImageIds)
                {
                    var record = await AppServices.Gateway.GetImageAsync(id, _closed.Token);
                    if (selectionVersion != version || _closed.IsCancellationRequested) return;
                    if (record == null) continue;
                    var image = new Image { Width = 128, Height = 100, ToolTip = System.IO.Path.GetFileName(record.Path) };
                    ThumbnailPresenter.SetItem(image, new ImageItemViewModel(record, _closed.Token, AppServices.GetClientServices()));
                    _examples.Children.Add(image);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { _feedback.Text = "代表照片读取失败，请重新选择建议。"; }
        };
        _candidates.ItemsSource = proposals.Where(p => p.State == "proposed").ToArray();
        if (_candidates.Items.Count > 0) _candidates.SelectedIndex = 0;
        else { _feedback.Text = "暂无待审核的新标签建议。"; actions.IsEnabled = false; }

        void AddButton(string title, string decision)
        {
            var button = new Button { Content = title, Padding = new Thickness(10,4,10,4), Margin = new Thickness(3) };
            actions.Children.Add(button);
            button.Click += async (_, _) =>
            {
                if (_candidates.SelectedItem is not ClassificationProposal p) return;
                var target = (_merge.SelectedItem as ClassificationLabel)?.Id;
                if (decision == "merge" && target == null) { _feedback.Text = "请先选择已有标签。"; return; }
                IsEnabled = false;
                try
                {
                    var review = new ClassificationProposalReview(Guid.NewGuid().ToString("N"), p.Id, p.Revision, taxonomy.Revision, decision, target);
                    if (decision == "accept") MatchJob = await client.AcceptAndMatch(review, _closed.Token);
                    else { await client.Synchronize(_closed.Token); await client.Review(review, _closed.Token); }
                    if (IsVisible) DialogResult = true;
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _feedback.Text = "处理建议失败：" + ex.Message; }
                finally { IsEnabled = true; }
            };
        }
    }
}
