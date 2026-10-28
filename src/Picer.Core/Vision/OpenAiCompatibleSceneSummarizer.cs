using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Picer.Core.Vision;

public sealed record SceneRepresentativeDescription(
    int Order, DateTime TakenUtc, string Summary, IReadOnlyList<string> Scenes,
    IReadOnlyList<string> Activities, IReadOnlyList<string> Objects);

public sealed record SceneSummaryResult(string SceneName, string Summary, IReadOnlyList<string> SearchTerms);

public interface ISceneGroupSummarizer : IDisposable
{
    string Provider { get; }
    string ModelVersion { get; }
    string PromptVersion { get; }
    Task<SceneSummaryResult> SummarizeAsync(IReadOnlyList<SceneRepresentativeDescription> descriptions,
        CancellationToken cancellationToken = default);
}

public sealed class OpenAiCompatibleSceneSummarizer : ISceneGroupSummarizer, IDisposable
{
    private readonly CloudVisionApiOptions _options;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public OpenAiCompatibleSceneSummarizer(CloudVisionApiOptions options, HttpClient? client = null, bool disposeClient = false)
    {
        _options = options;
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _ownsClient = client is null || disposeClient;
    }

    public string Provider => _options.Provider;
    public string ModelVersion => _options.Model;
    public string PromptVersion => "picer-scene-summary-v1";

    public async Task<SceneSummaryResult> SummarizeAsync(IReadOnlyList<SceneRepresentativeDescription> descriptions,
        CancellationToken cancellationToken = default)
    {
        if (descriptions.Count == 0) throw new ArgumentException("至少需要一条代表照片描述。", nameof(descriptions));
        var orderedJson = JsonSerializer.Serialize(descriptions.OrderBy(item => item.Order));
        var payload = new
        {
            model = _options.Model,
            temperature = 0,
            max_tokens = 500,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = "你是私人照片库的场景总结助手。根据按时间排序的代表照片描述，概括它们共同构成的事件或场景。只输出JSON，字段必须为sceneName、summary、searchTerms；searchTerms必须是字符串数组。不得猜测人物姓名、身份或关系。" },
                new { role = "user", content = "以下代表照片已按拍摄顺序排列。请综合上下文生成一个简洁场景名称、整体摘要和搜索词，不要逐张复述：\n" + orderedJson }
            }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var content = OpenAiCompatibleCloudVisionAnalyzer.ExtractResponseContent(responseText);
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("场景总结模型返回了空内容。");
        var result = JsonSerializer.Deserialize<SceneSummaryResult>(StripFence(content), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (result is null || string.IsNullOrWhiteSpace(result.SceneName) || string.IsNullOrWhiteSpace(result.Summary))
            throw new InvalidDataException("场景总结模型没有返回有效的场景名称和摘要。");
        return result with { SearchTerms = result.SearchTerms ?? [] };
    }

    private static string StripFence(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        var firstNewline = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline >= 0 && lastFence > firstNewline ? trimmed[(firstNewline + 1)..lastFence].Trim() : trimmed;
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
