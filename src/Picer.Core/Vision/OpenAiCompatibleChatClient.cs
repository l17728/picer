using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace Picer.Core.Vision;

public sealed class OpenAiCompatibleChatClient(Uri endpoint, string apiKey, string model, HttpClient? client = null, bool disposeClient = false) : IDisposable
{
    private readonly HttpClient _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    private readonly bool _ownsClient = client is null || disposeClient;
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

    public async IAsyncEnumerable<string> StreamAsync(string systemPrompt, string userPrompt,
        int maximumTokens = 800, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payload = new { model, temperature = 0, max_tokens = maximumTokens, stream = true,
            messages = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userPrompt } } };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(":", StringComparison.Ordinal)) continue;
            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) line = line[5..].TrimStart();
            if (line == "[DONE]") yield break;
            string? text = null;
            try
            {
                using var document = JsonDocument.Parse(line);
                var choices = document.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() == 0) continue;
                var choice = choices[0];
                if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var value))
                {
                    text = value.GetString();
                }
                else if (choice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var full))
                {
                    text = full.GetString();
                }
            }
            catch (JsonException) { }
            if (!string.IsNullOrEmpty(text)) yield return text;
        }
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
