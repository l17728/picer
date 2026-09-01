using System.IO;
using System.Text.Json;
using Picer.Core.Vision;

namespace Picer.App;

public enum AiModelCapability { Language, Multimodal }
public static class AiFeatureIds
{
    public const string PhotoAnalysis = "photo-analysis";
    public const string VideoSummary = "video-summary";
    public const string SceneDescription = "scene-description";
    public const string AiQuestion = "ai-question";
}
public sealed class AiModelDefinition { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public AiModelCapability Capability { get; set; } }
public sealed class AiProviderDefinition { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string BaseUrl { get; set; } = ""; public List<AiModelDefinition> Models { get; set; } = []; }
public sealed class AiFeatureBinding { public string ProviderId { get; set; } = ""; public string ModelId { get; set; } = ""; }

public sealed class CloudVisionSettings
{
    public bool Enabled { get; set; }
    public DomesticVisionProvider Provider { get; set; } = DomesticVisionProvider.BailianQwen;
    public int MaximumImagesPerRun { get; set; } = 100;
    public bool IncludePreciseGps { get; set; }
    public bool UseComplexModel { get; set; }
    public decimal DailyBudgetYuan { get; set; } = 2m;
    public decimal MonthlyBudgetYuan { get; set; } = 20m;
    public List<AiProviderDefinition> Providers { get; set; } = [];
    public Dictionary<string, AiFeatureBinding> Features { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static CloudVisionSettings Load(string path)
    {
        CloudVisionSettings settings;
        try { settings = File.Exists(path) ? JsonSerializer.Deserialize<CloudVisionSettings>(File.ReadAllText(path)) ?? new() : new(); }
        catch { settings = new(); }
        settings.EnsureDefaults(); return settings;
    }

    public void EnsureDefaults()
    {
        Providers ??= []; Features ??= new(StringComparer.OrdinalIgnoreCase);
        AddProviderIfMissing(new() { Id="bailian", Name="阿里云百炼", BaseUrl="https://dashscope.aliyuncs.com/compatible-mode/v1", Models=[new(){Id="qwen3-vl-8b-instruct",Name="Qwen3 VL 8B",Capability=AiModelCapability.Multimodal},new(){Id="qwen3-vl-plus",Name="Qwen3 VL Plus",Capability=AiModelCapability.Multimodal}] });
        AddProviderIfMissing(new() { Id="zhipu", Name="智谱", BaseUrl="https://open.bigmodel.cn/api/paas/v4", Models=[new(){Id="glm-4.6v-flash",Name="GLM-4.6V-Flash",Capability=AiModelCapability.Multimodal}] });
        AddProviderIfMissing(new() { Id="huawei", Name="华为", Models=[] });
        var provider = Provider == DomesticVisionProvider.ZhipuGlm ? "zhipu" : "bailian";
        var model = provider == "zhipu" ? "glm-4.6v-flash" : UseComplexModel ? "qwen3-vl-plus" : "qwen3-vl-8b-instruct";
        AddFeatureIfMissing(AiFeatureIds.PhotoAnalysis, provider, model); AddFeatureIfMissing(AiFeatureIds.VideoSummary, provider, model);
        AddFeatureIfMissing(AiFeatureIds.SceneDescription, provider, model);
    }

    public void MergeProviders(IEnumerable<AiProviderDefinition> providers)
    {
        foreach(var imported in providers)
        {
            var existing=Providers.FirstOrDefault(x=>x.Id.Equals(imported.Id,StringComparison.OrdinalIgnoreCase));
            if(existing is null){Providers.Add(imported);continue;} existing.Name=imported.Name; existing.BaseUrl=imported.BaseUrl;
            foreach(var model in imported.Models){var old=existing.Models.FirstOrDefault(x=>x.Id.Equals(model.Id,StringComparison.OrdinalIgnoreCase));if(old is null)existing.Models.Add(model);else{old.Name=model.Name;old.Capability=model.Capability;}}
        }
    }

    public (AiProviderDefinition Provider, AiModelDefinition Model)? Resolve(string featureId)
    {
        if(!Features.TryGetValue(featureId,out var binding))return null;
        var provider=Providers.FirstOrDefault(x=>x.Id.Equals(binding.ProviderId,StringComparison.OrdinalIgnoreCase));
        var model=provider?.Models.FirstOrDefault(x=>x.Id.Equals(binding.ModelId,StringComparison.OrdinalIgnoreCase));
        return provider is null||model is null?null:(provider,model);
    }

    public void Save(string path)
    {
        MaximumImagesPerRun=Math.Clamp(MaximumImagesPerRun,1,1000);DailyBudgetYuan=Math.Clamp(DailyBudgetYuan,.1m,10000m);MonthlyBudgetYuan=Math.Clamp(MonthlyBudgetYuan,DailyBudgetYuan,100000m);
        var directory=Path.GetDirectoryName(path);if(!string.IsNullOrWhiteSpace(directory))Directory.CreateDirectory(directory);var temporary=path+".tmp";
        File.WriteAllText(temporary,JsonSerializer.Serialize(this,new JsonSerializerOptions{WriteIndented=true}));File.Move(temporary,path,true);
    }
    private void AddProviderIfMissing(AiProviderDefinition value){if(!Providers.Any(x=>x.Id.Equals(value.Id,StringComparison.OrdinalIgnoreCase)))Providers.Add(value);}
    private void AddFeatureIfMissing(string id,string provider,string model){if(!Features.ContainsKey(id))Features[id]=new(){ProviderId=provider,ModelId=model};}
}
