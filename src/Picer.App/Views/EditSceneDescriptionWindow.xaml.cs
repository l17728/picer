using System.Windows;

namespace Picer.App.Views;

public partial class EditSceneDescriptionWindow : Window
{
    public string SceneTitle => TitleText.Text.Trim();
    public string SceneSummary => SummaryText.Text.Trim();

    public EditSceneDescriptionWindow(string title, string summary)
    {
        InitializeComponent();
        TitleText.Text = title;
        SummaryText.Text = summary;
        Loaded += (_, _) => { TitleText.Focus(); TitleText.SelectAll(); };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleText.Text))
        {
            MessageBox.Show("场景标题不能为空。", "编辑场景", MessageBoxButton.OK, MessageBoxImage.Information);
            TitleText.Focus();
            return;
        }
        DialogResult = true;
    }
}
