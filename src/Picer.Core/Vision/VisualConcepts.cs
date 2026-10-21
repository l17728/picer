using Picer.Core.Models;

namespace Picer.Core.Vision;

public sealed record VisualConceptScore(string ConceptId, float Confidence);
public sealed record SemanticAnalysisResult(IReadOnlyList<VisualConceptScore> Concepts, string? Caption = null,
    float[]? Embedding = null, string? EmbeddingModelVersion = null);
public sealed record VisualQueryParse(IReadOnlyList<string> ConceptIds, string RemainingQuery);
public sealed record SemanticBatchResult(int Requested, int Analyzed, int Skipped, int Failed);

public interface IImageSemanticAnalyzer
{
    string AnalyzerName { get; }
    string ModelVersion { get; }
    Task<SemanticAnalysisResult> AnalyzeAsync(ImageRecord image, CancellationToken cancellationToken);
}

public static class VisualConceptCatalog
{
    public static IReadOnlyDictionary<string, string> DisplayNames => Terms.ToDictionary(pair => pair.Key, pair => pair.Value[0]);
    private static readonly IReadOnlyDictionary<string, string[]> Terms = new Dictionary<string, string[]>
    {
        ["metadata.orientation.landscape"] = ["横向照片"],
        ["metadata.orientation.portrait"] = ["纵向照片"],
        ["metadata.orientation.square"] = ["方形照片"],
        ["metadata.camera.present"] = ["包含相机信息"],
        ["metadata.gps.present"] = ["包含 GPS 坐标"],
        ["subject.people"] = ["人物", "有人", "人像"],
        ["subject.group"] = ["多人", "合影", "集体照"],
        ["subject.animal"] = ["动物", "宠物"],
        ["object.vehicle"] = ["汽车", "车辆", "轿车"],
        ["object.food"] = ["食物", "美食", "菜肴"],
        ["object.flower"] = ["花卉", "鲜花", "花朵"],
        ["object.building"] = ["建筑", "房屋"],
        ["document.general"] = ["文档", "纸质文件", "文字材料"],
        ["document.identity"] = ["证件", "身份证件", "卡片证件"],
        ["document.screenshot"] = ["屏幕截图", "截图"],
        ["scene.landscape"] = ["风景", "自然风光"],
        ["scene.city"] = ["城市", "城市街景"],
        ["scene.indoor"] = ["室内", "室内场景"],
        ["scene.outdoor"] = ["户外", "室外场景"],
        ["scene.night"] = ["夜景", "夜晚"],
        ["season.summer"] = ["夏天", "夏季"],
        ["apparel.dress"] = ["连衣裙", "裙子", "长裙", "短裙"],
        ["scene.mountain"] = ["山上", "山里", "山地", "山区", "山景"],
        ["scene.beach"] = ["海边", "沙滩", "海滩"],
        ["scene.snow"] = ["雪地", "下雪", "雪景"],
        ["activity.hiking"] = ["徒步", "爬山", "登山"],
        ["activity.dining"] = ["聚餐", "吃饭", "餐桌"]
    };

    public static IReadOnlyList<(string ConceptId, string Prompt, string NegativePrompt, float Threshold)> ContentConcepts { get; } =
    [
        ("subject.people", "一张清楚包含人物的照片", "一张完全没有人物的照片", 0.62f),
        ("subject.group", "一张包含多个人的合影", "一张没有多人合影的照片", 0.66f),
        ("subject.animal", "一张以动物或宠物为内容的照片", "一张完全没有动物的照片", 0.64f),
        ("object.vehicle", "一张清楚包含汽车或车辆的照片", "一张完全没有汽车或车辆的照片", 0.64f),
        ("object.food", "一张以食物或菜肴为内容的照片", "一张完全没有食物的照片", 0.64f),
        ("object.flower", "一张以花卉或花朵为内容的照片", "一张完全没有花卉的照片", 0.65f),
        ("object.building", "一张清楚包含建筑物的照片", "一张完全没有建筑物的照片", 0.63f),
        ("document.general", "一张文档、纸张或文字材料的照片", "一张自然场景且不是文档的照片", 0.65f),
        ("document.identity", "一张身份证、护照、驾驶证或其他证件的照片", "一张不包含任何证件的普通照片", 0.69f),
        ("document.screenshot", "一张手机或电脑的屏幕截图", "一张相机拍摄的现实世界照片", 0.68f),
        ("scene.landscape", "一张自然风景或风光照片", "一张没有自然风景的照片", 0.63f),
        ("scene.city", "一张城市或街道场景的照片", "一张不在城市或街道的照片", 0.63f),
        ("scene.indoor", "一张在室内拍摄的照片", "一张在室外拍摄的照片", 0.62f),
        ("scene.outdoor", "一张在户外拍摄的照片", "一张在室内拍摄的照片", 0.62f),
        ("scene.night", "一张夜晚或夜景照片", "一张白天拍摄的照片", 0.64f),
        ("scene.mountain", "一张包含山峰或山地的照片", "一张完全没有山的照片", 0.64f),
        ("scene.beach", "一张海边、海滩或沙滩照片", "一张完全没有海滩的照片", 0.65f),
        ("scene.snow", "一张包含雪地或雪景的照片", "一张完全没有雪的照片", 0.66f),
        ("activity.hiking", "一张徒步、爬山或登山活动照片", "一张没有徒步或登山活动的照片", 0.67f),
        ("activity.dining", "一张聚餐、吃饭或餐桌场景照片", "一张没有用餐活动的照片", 0.65f)
    ];

    public static bool TryResolve(string? value, out string conceptId)
    {
        var normalized = value?.Trim();
        if (!string.IsNullOrEmpty(normalized))
        {
            foreach (var pair in Terms)
            {
                if (pair.Key.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                    pair.Value.Any(term => term.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    conceptId = pair.Key;
                    return true;
                }
            }
        }
        conceptId = "";
        return false;
    }

    public static string DisplayName(string conceptId) =>
        DisplayNames.TryGetValue(conceptId, out var value) ? value : conceptId;

    public static VisualQueryParse Parse(string? query)
    {
        var remaining = query?.Trim() ?? "";
        var concepts = new List<string>();
        foreach (var entry in Terms.SelectMany(pair => pair.Value.Select(term => (Concept: pair.Key, Term: term)))
                     .OrderByDescending(value => value.Term.Length))
        {
            if (!remaining.Contains(entry.Term, StringComparison.OrdinalIgnoreCase)) continue;
            if (!concepts.Contains(entry.Concept, StringComparer.Ordinal)) concepts.Add(entry.Concept);
            remaining = remaining.Replace(entry.Term, "", StringComparison.OrdinalIgnoreCase);
        }
        remaining = remaining
            .Replace("穿着", "", StringComparison.OrdinalIgnoreCase)
            .Replace("穿", "", StringComparison.OrdinalIgnoreCase)
            .Replace("并且", "", StringComparison.OrdinalIgnoreCase)
            .Replace("而且", "", StringComparison.OrdinalIgnoreCase)
            .Replace("在", "", StringComparison.OrdinalIgnoreCase)
            .Trim(' ', '，', ',', '。', '的');
        return new VisualQueryParse(concepts, remaining);
    }
}
