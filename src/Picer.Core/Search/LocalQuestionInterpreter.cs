namespace Picer.Core.Search;

public enum LocalQuestionIntent { Count, CameraSummary, Unsupported }

public sealed record LocalQuestionResult(LocalQuestionIntent Intent, string Explanation)
{
    public bool Supported => Intent != LocalQuestionIntent.Unsupported;
}

public static class LocalQuestionInterpreter
{
    public static LocalQuestionResult Interpret(string question)
    {
        var text = question.Trim();
        if (text.Contains("多少", StringComparison.OrdinalIgnoreCase) || text.Contains("几张", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("count", StringComparison.OrdinalIgnoreCase))
            return new(LocalQuestionIntent.Count, "依据当前结构化筛选统计本地索引记录");
        if (text.Contains("相机", StringComparison.OrdinalIgnoreCase) || text.Contains("camera", StringComparison.OrdinalIgnoreCase))
            return new(LocalQuestionIntent.CameraSummary, "依据当前结果的相机 EXIF 元数据汇总");
        return new(LocalQuestionIntent.Unsupported, "当前本地问答仅支持数量和相机汇总；可切换到检索模式搜索文件名、相机或 GPS");
    }
}
