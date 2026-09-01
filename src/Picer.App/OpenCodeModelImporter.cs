using System.IO;
using System.Text.Json;
namespace Picer.App;
public sealed record OpenCodeImportResult(IReadOnlyList<AiProviderDefinition> Providers,IReadOnlyDictionary<string,string> ApiKeys,string Path);
public static class OpenCodeModelImporter
{
    public static string DefaultConfigPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".config","opencode","opencode.jsonc");
    public static OpenCodeImportResult Import(string? path=null)
    {
        path??=DefaultConfigPath;if(!File.Exists(path))throw new FileNotFoundException("未找到 OpenCode 配置文件。",path);
        using var document=JsonDocument.Parse(File.ReadAllText(path),new JsonDocumentOptions{CommentHandling=JsonCommentHandling.Skip,AllowTrailingCommas=true});
        if(!document.RootElement.TryGetProperty("provider",out var root))return new([],new Dictionary<string,string>(),path);
        var providers=new List<AiProviderDefinition>();var keys=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var property in root.EnumerateObject())
        {
            var value=property.Value;var name=Read(value,"name")??property.Name;var options=value.TryGetProperty("options",out var o)?o:default;
            var baseUrl=options.ValueKind==JsonValueKind.Object?Read(options,"baseURL")??Read(options,"baseUrl")??"":"";var key=options.ValueKind==JsonValueKind.Object?Read(options,"apiKey"):null;
            var models=new List<AiModelDefinition>();if(value.TryGetProperty("models",out var modelRoot))foreach(var model in modelRoot.EnumerateObject()){var modelName=Read(model.Value,"name")??model.Name;models.Add(new(){Id=model.Name,Name=modelName,Capability=ReadCapability(model.Value,model.Name,modelName)});}
            providers.Add(new(){Id=property.Name,Name=name,BaseUrl=baseUrl.TrimEnd('/'),Models=models});if(!string.IsNullOrWhiteSpace(key))keys[property.Name]=key;
        }
        return new(providers,keys,path);
    }
    public static AiModelCapability InferCapability(params string[] names){var value=string.Join(' ',names);string[] hints=["vision","vl","ocr","multimodal","4.6v","4v","视觉","多模态"];return hints.Any(x=>value.Contains(x,StringComparison.OrdinalIgnoreCase))?AiModelCapability.Multimodal:AiModelCapability.Language;}
    private static AiModelCapability ReadCapability(JsonElement model,params string[] names)
    {
        if(model.TryGetProperty("attachment",out var attachment)&&attachment.ValueKind==JsonValueKind.True)return AiModelCapability.Multimodal;
        if(model.TryGetProperty("modalities",out var modalities)&&modalities.ToString().Contains("image",StringComparison.OrdinalIgnoreCase))return AiModelCapability.Multimodal;
        return InferCapability(names);
    }
    private static string? Read(JsonElement element,string property)=>element.TryGetProperty(property,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;
}
