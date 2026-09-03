using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Picer.App.Security;

namespace Picer.App;

public partial class CloudVisionSettingsWindow : Window
{
    private readonly string _settingsPath; private readonly ICredentialStore _credentials; private readonly CloudVisionSettings _settings;
    private bool _loading; private FeatureItem? _currentFeature; private string? _currentProviderId;
    private readonly FeatureItem[] _features=
    [
        new(AiFeatureIds.PhotoAnalysis,"照片内容分析","多模态模型","识别场景、活动、物体并生成可搜索标签。",AiModelCapability.Multimodal),
        new(AiFeatureIds.VideoSummary,"视频内容总结","多模态模型","分析视频关键帧拼图并结合本地转写生成摘要。",AiModelCapability.Multimodal),
        new(AiFeatureIds.SceneDescription,"未分类场景描述","多模态模型","分析代表照片，为时间场景生成名称和检索词。",AiModelCapability.Multimodal)
        ,new(AiFeatureIds.VideoTranscription,"视频语音转文字","语音模型","从视频提取完整音频，使用所选本地语音模型转写。",AiModelCapability.Language)
        ,new(AiFeatureIds.AiQuestion,"AI 问答与媒体检索","多模态模型","拆解自然语言条件，检索照片及可选的视频，并复核候选结果。",AiModelCapability.Multimodal)
    ];

    public CloudVisionSettingsWindow(string settingsPath,ICredentialStore? credentials=null)
    {
        InitializeComponent();_settingsPath=settingsPath;_credentials=credentials??new WindowsCredentialStore();_settings=CloudVisionSettings.Load(settingsPath);
        EnabledBox.IsChecked=_settings.Enabled;MaximumBox.Text=_settings.MaximumImagesPerRun.ToString();PreciseGpsBox.IsChecked=_settings.IncludePreciseGps;
        DailyBudgetBox.Text=_settings.DailyBudgetYuan.ToString("0.##");MonthlyBudgetBox.Text=_settings.MonthlyBudgetYuan.ToString("0.##");FeatureBox.ItemsSource=_features;FeatureBox.SelectedIndex=0;
    }

    private void FeatureBox_SelectionChanged(object sender,SelectionChangedEventArgs e){SaveCurrentSelection();_currentFeature=FeatureBox.SelectedItem as FeatureItem;LoadFeature();}
    private void LoadFeature()
    {
        if(_currentFeature is null)return;_loading=true;FeatureTitle.Text=$"{_currentFeature.Name} · {_currentFeature.TypeLabel}";
        var providers=_settings.Providers.OrderBy(x=>x.Name).ToArray();ProviderBox.ItemsSource=providers;
        _settings.Features.TryGetValue(_currentFeature.Id,out var binding);ProviderBox.SelectedItem=providers.FirstOrDefault(x=>x.Id.Equals(binding?.ProviderId,StringComparison.OrdinalIgnoreCase))??providers.FirstOrDefault();_loading=false;LoadModels(binding?.ModelId);
    }
    private void ProviderBox_SelectionChanged(object sender,SelectionChangedEventArgs e){if(!_loading){PersistPendingKey();LoadModels(null);}}
    private void LoadModels(string? selectedModel)
    {
        var provider=ProviderBox.SelectedItem as AiProviderDefinition;_currentProviderId=provider?.Id;EndpointText.Text=provider is null?"未选择服务商":$"接口：{provider.BaseUrl}";
        var models=provider?.Models.Select(x=>new ModelItem(x)).ToArray()??[];ModelBox.ItemsSource=models;
        ModelBox.SelectedItem=models.FirstOrDefault(x=>x.Model.Id.Equals(selectedModel,StringComparison.OrdinalIgnoreCase))??models.FirstOrDefault();NoModelText.Visibility=models.Length==0?Visibility.Visible:Visibility.Collapsed;ApiKeyBox.Clear();RefreshKeyStatus();
    }
    private void SaveCurrentSelection()
    {
        if(_loading||_currentFeature is null)return;var provider=ProviderBox.SelectedItem as AiProviderDefinition;var model=ModelBox.SelectedItem as ModelItem;
        _settings.Features[_currentFeature.Id]=new(){ProviderId=provider?.Id??"",ModelId=model?.Model.Id??""};
        if(provider is not null&&!string.IsNullOrWhiteSpace(ApiKeyBox.Password))_credentials.Write(PicerCredentialTargets.AiProvider(provider.Id),ApiKeyBox.Password.Trim());
    }
    private void PersistPendingKey(){if(!string.IsNullOrWhiteSpace(_currentProviderId)&&!string.IsNullOrWhiteSpace(ApiKeyBox.Password))_credentials.Write(PicerCredentialTargets.AiProvider(_currentProviderId),ApiKeyBox.Password.Trim());}
    private void RefreshKeyStatus()
    {
        if (ProviderBox.SelectedItem is AiProviderDefinition local && AiProviderIds.IsLocal(local.Id))
        {
            ApiKeyBox.IsEnabled = false;
            KeyStatusText.Text = "✓ 本地代理无需 API Key";
            KeyStatusText.Foreground = new SolidColorBrush(Color.FromRgb(33,115,70));
            return;
        }
        ApiKeyBox.IsEnabled = true;
        var provider=ProviderBox.SelectedItem as AiProviderDefinition;var saved=provider is not null&&ReadProviderKey(provider.Id) is {Length:>0};KeyStatusText.Text=saved?"✓ 密钥已安全保存":"尚未保存密钥";KeyStatusText.Foreground=new SolidColorBrush(saved?Color.FromRgb(33,115,70):Color.FromRgb(160,70,45));
    }
    private string? ReadProviderKey(string id)
    {
        var current=_credentials.Read(PicerCredentialTargets.AiProvider(id));if(!string.IsNullOrWhiteSpace(current))return current;
        return id.Equals("bailian",StringComparison.OrdinalIgnoreCase)?_credentials.Read(PicerCredentialTargets.Bailian):id.Equals("zhipu",StringComparison.OrdinalIgnoreCase)?_credentials.Read(PicerCredentialTargets.Zhipu):null;
    }
    private void DeleteKey_Click(object sender,RoutedEventArgs e){if(ProviderBox.SelectedItem is not AiProviderDefinition provider)return;_credentials.Delete(PicerCredentialTargets.AiProvider(provider.Id));if(provider.Id=="bailian")_credentials.Delete(PicerCredentialTargets.Bailian);if(provider.Id=="zhipu")_credentials.Delete(PicerCredentialTargets.Zhipu);ApiKeyBox.Clear();RefreshKeyStatus();}
    private void ImportOpenCode_Click(object sender,RoutedEventArgs e)
    {
        try{SaveCurrentSelection();var result=OpenCodeModelImporter.Import();_settings.MergeProviders(result.Providers);foreach(var key in result.ApiKeys)_credentials.Write(PicerCredentialTargets.AiProvider(key.Key),key.Value);ImportStatusText.Text=$"已同步 {result.Providers.Count} 个服务商、{result.Providers.Sum(x=>x.Models.Count)} 个模型";LoadFeature();}
        catch(Exception ex){MessageBox.Show(this,ex.Message,"OpenCode 同步",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
    private void Save_Click(object sender,RoutedEventArgs e)
    {
        SaveCurrentSelection();if(!int.TryParse(MaximumBox.Text,out var maximum)||maximum is<1 or>1000){Warn("单批上限必须是 1 到 1000 之间的整数。");return;}
        if(!decimal.TryParse(DailyBudgetBox.Text,out var daily)||daily<.1m||!decimal.TryParse(MonthlyBudgetBox.Text,out var monthly)||monthly<daily){Warn("每日预算至少 0.1 元；每月预算不得低于每日预算。");return;}
        if(EnabledBox.IsChecked==true)
            foreach(var feature in _features){var resolved=_settings.Resolve(feature.Id);if(resolved is null){Warn($"请为“{feature.Name}”选择模型。");return;}if(!AiProviderIds.IsLocal(resolved.Value.Provider.Id)&&ReadProviderKey(resolved.Value.Provider.Id) is not{Length:>0}){Warn($"“{feature.Name}”使用的服务商尚未保存 API Key。");return;}}
        _settings.Enabled=EnabledBox.IsChecked==true;_settings.MaximumImagesPerRun=maximum;_settings.IncludePreciseGps=PreciseGpsBox.IsChecked==true;_settings.DailyBudgetYuan=daily;_settings.MonthlyBudgetYuan=monthly;_settings.Save(_settingsPath);DialogResult=true;
    }
    private void Warn(string message)=>MessageBox.Show(this,message,"AI 模型设置",MessageBoxButton.OK,MessageBoxImage.Warning);
    private sealed record FeatureItem(string Id,string Name,string TypeLabel,string Description,AiModelCapability Capability);
    private sealed record ModelItem(AiModelDefinition Model){public string DisplayName=>$"{Model.Name}  ({Model.Id})";}
}
