using System.Windows;
using System.Windows.Controls;
using Picer.App.Security;
using Picer.Core.Vision;

namespace Picer.App;

public partial class CloudVisionSettingsWindow : Window
{
    private readonly string _settingsPath;
    private readonly ICredentialStore _credentials;
    private readonly CloudVisionSettings _settings;

    public CloudVisionSettingsWindow(string settingsPath, ICredentialStore? credentials = null)
    {
        InitializeComponent(); _settingsPath = settingsPath; _credentials = credentials ?? new WindowsCredentialStore();
        _settings = CloudVisionSettings.Load(settingsPath); EnabledBox.IsChecked = _settings.Enabled;
        ProviderBox.SelectedIndex = _settings.Provider == DomesticVisionProvider.BailianQwen ? 0 : 1;
        MaximumBox.Text = _settings.MaximumImagesPerRun.ToString(); PreciseGpsBox.IsChecked = _settings.IncludePreciseGps;
        ComplexModelBox.IsChecked = _settings.UseComplexModel; DailyBudgetBox.Text = _settings.DailyBudgetYuan.ToString("0.##");
        MonthlyBudgetBox.Text = _settings.MonthlyBudgetYuan.ToString("0.##"); RefreshKeyStatus();
    }

    private DomesticVisionProvider SelectedProvider => ProviderBox.SelectedIndex == 1 ? DomesticVisionProvider.ZhipuGlm : DomesticVisionProvider.BailianQwen;
    private string CredentialTarget => SelectedProvider == DomesticVisionProvider.BailianQwen ? PicerCredentialTargets.Bailian : PicerCredentialTargets.Zhipu;
    private void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (KeyStatusText is not null) RefreshKeyStatus(); }
    private void RefreshKeyStatus()
    {
        var saved = _credentials.Read(CredentialTarget) is { Length: > 0 };
        KeyStatusText.Text = saved
            ? "✓ Key 已安全保存，关闭或重启程序后仍有效；输入框留空即可。"
            : "尚未保存 Key，请输入后点击“保存”。";
        KeyStatusText.Foreground = saved
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(33, 115, 70))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 70, 45));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MaximumBox.Text, out var maximum) || maximum is < 1 or > 1000) { MessageBox.Show(this, "单批上限必须是1到1000之间的整数。", "设置", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!decimal.TryParse(DailyBudgetBox.Text, out var daily) || daily < .1m || !decimal.TryParse(MonthlyBudgetBox.Text, out var monthly) || monthly < daily)
        { MessageBox.Show(this, "每日预算至少0.1元；每月预算不得低于每日预算。", "设置", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!string.IsNullOrWhiteSpace(ApiKeyBox.Password)) _credentials.Write(CredentialTarget, ApiKeyBox.Password.Trim());
        if (EnabledBox.IsChecked == true && _credentials.Read(CredentialTarget) is not { Length: > 0 }) { MessageBox.Show(this, "启用云端分析前请填写并保存API Key。", "设置", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        _settings.Enabled = EnabledBox.IsChecked == true; _settings.Provider = SelectedProvider; _settings.MaximumImagesPerRun = maximum;
        _settings.IncludePreciseGps = PreciseGpsBox.IsChecked == true; _settings.UseComplexModel = ComplexModelBox.IsChecked == true;
        _settings.DailyBudgetYuan = daily; _settings.MonthlyBudgetYuan = monthly;
        _settings.Save(_settingsPath); DialogResult = true;
    }

    private void DeleteKey_Click(object sender, RoutedEventArgs e) { _credentials.Delete(CredentialTarget); ApiKeyBox.Clear(); RefreshKeyStatus(); }
}
