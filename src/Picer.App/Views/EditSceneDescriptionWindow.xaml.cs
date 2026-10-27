using System.Windows;

namespace Picer.App.Views;

public partial class EditSceneDescriptionWindow : Window
{
    public Func<Task>? SaveAsync { get; set; }
    private bool _saving;
    public string SceneTitle => TitleText.Text.Trim();
    public string SceneSummary => SummaryText.Text.Trim();

    public EditSceneDescriptionWindow(string title, string summary)
    {
        InitializeComponent();
        TitleText.Text = title;
        SummaryText.Text = summary;
        Loaded += (_, _) => { TitleText.Focus(); TitleText.SelectAll(); };
        Closing += (_,args) => { if(_saving)args.Cancel=true; };
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if(_saving)return;
        if (string.IsNullOrWhiteSpace(TitleText.Text))
        {
            MessageBox.Show("场景标题不能为空。", "编辑场景", MessageBoxButton.OK, MessageBoxImage.Information);
            TitleText.Focus();
            return;
        }
        _saving=true;IsEnabled=false;SaveError.Text="";
        try
        {
            if(SaveAsync is not null)await SaveAsync();
            _saving=false;DialogResult=true;
        }
        catch(Exception exception)
        {
            SaveError.Text="保存失败，输入内容已保留。"+exception.Message;
        }
        finally { _saving=false;IsEnabled=true; }
    }
}
