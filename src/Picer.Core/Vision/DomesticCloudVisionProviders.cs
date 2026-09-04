namespace Picer.Core.Vision;

public enum DomesticVisionProvider
{
    BailianQwen,
    ZhipuGlm
}

public static class DomesticCloudVisionProviders
{
    public static CloudVisionApiOptions Bailian(string apiKey, bool complex = false) => new(
        new Uri("https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions"),
        apiKey,
        complex ? "qwen3-vl-plus" : "qwen3-vl-8b-instruct",
        "bailian",
        PromptVersion: "picer-photo-v2-concise");

    public static CloudVisionApiOptions ZhipuFree(string apiKey) => new(
        new Uri("https://open.bigmodel.cn/api/paas/v4/chat/completions"),
        apiKey,
        "glm-4.6v-flash",
        "zhipu",
        PromptVersion: "picer-photo-v2-concise");
}

public static class DomesticCloudVisionPricing
{
    public static CloudVisionUsage Calculate(string provider, string model, long inputTokens, long outputTokens)
    {
        decimal inputPerMillion; decimal outputPerMillion;
        if (provider == "bailian" && model.Contains("plus", StringComparison.OrdinalIgnoreCase))
            (inputPerMillion, outputPerMillion) = (1m, 10m);
        else if (provider == "bailian")
            (inputPerMillion, outputPerMillion) = (0.5m, 2m);
        else
            (inputPerMillion, outputPerMillion) = (0m, 0m);
        var cost = inputTokens * inputPerMillion / 1_000_000m + outputTokens * outputPerMillion / 1_000_000m;
        return new CloudVisionUsage(inputTokens, outputTokens, cost);
    }
}

public sealed record CloudVisionRunPolicy(
    bool Enabled = false,
    int MaximumImagesPerRun = 100,
    bool IncludePreciseGps = false,
    decimal DailyBudgetYuan = 2m,
    decimal MonthlyBudgetYuan = 20m)
{
    public int ClampCount(int requested) => !Enabled ? 0 : Math.Clamp(requested, 0, Math.Max(0, MaximumImagesPerRun));
}
