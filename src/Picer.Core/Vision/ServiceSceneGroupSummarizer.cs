using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Picer.Core.Vision;

public sealed class ServiceSceneGroupSummarizer(Uri serviceEndpoint, string bearerToken, string model = "service-vl", HttpClient? client = null) : ISceneGroupSummarizer, IDisposable
{
    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    private readonly bool _ownsClient = client is null;
    public string Provider => "service";
    public string ModelVersion => model;
    public string PromptVersion => "picer-scene-summary-v1";

    public async Task<SceneSummaryResult> SummarizeAsync(IReadOnlyList<SceneRepresentativeDescription> descriptions, CancellationToken cancellationToken = default)
    {
        if (descriptions.Count == 0) throw new ArgumentException("descriptions_required", nameof(descriptions));
        var payload = JsonSerializer.Serialize(new { operation = "scene_summary", model, descriptions = descriptions.OrderBy(value => value.Order) });
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(serviceEndpoint, "api/service/v1/vl")) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        if (!string.IsNullOrWhiteSpace(bearerToken)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Service VL 请求失败（{(int)response.StatusCode}）：{text}");
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            text = choices[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        var result = JsonSerializer.Deserialize<SceneSummaryResult>(text.Trim('`', ' ', '\n'), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return result is null || string.IsNullOrWhiteSpace(result.SceneName) || string.IsNullOrWhiteSpace(result.Summary)
            ? throw new InvalidDataException("Service VL 返回的场景摘要无效") : result with { SearchTerms = result.SearchTerms ?? [] };
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
