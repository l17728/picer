using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Picer.Core.Vision;

public sealed class OpenAiCompatibleChatClient(Uri endpoint, string apiKey, string model, HttpClient? client = null) : IDisposable
{
    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    private readonly bool _ownsClient = client is null;
    public string Model => model;

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, int maximumTokens = 800,
        CancellationToken cancellationToken = default)
    {
        var payload = new { model, temperature = 0, max_tokens = maximumTokens,
            messages = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userPrompt } } };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"AI 请求失败（{(int)response.StatusCode}）：{responseText}");
        using var document = JsonDocument.Parse(responseText);
        var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        return string.IsNullOrWhiteSpace(content) ? throw new InvalidDataException("AI 返回了空内容。") : StripFence(content);
    }

    public static string StripFence(string value)
    {
        var text = value.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
        var first = text.IndexOf('\n'); var last = text.LastIndexOf("```", StringComparison.Ordinal);
        return first >= 0 && last > first ? text[(first + 1)..last].Trim() : text;
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
