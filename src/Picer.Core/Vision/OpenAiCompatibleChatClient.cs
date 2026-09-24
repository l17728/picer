using System.Net.Http.Headers;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using Picer.Core.Logging;

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
        var json = JsonSerializer.Serialize(payload);
        var requestId = Guid.NewGuid().ToString("N");
        using var response = await SendWithRetryAsync(() => CreateRequest(json, requestId), requestId, cancellationToken).ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI 请求失败（{(int)response.StatusCode}）：{(string.IsNullOrWhiteSpace(responseText) ? response.ReasonPhrase : responseText)}");
        using var document = JsonDocument.Parse(responseText);
        var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        return string.IsNullOrWhiteSpace(content) ? throw new InvalidDataException("AI 返回了空内容。") : StripFence(content);
    }

    public async IAsyncEnumerable<string> StreamAsync(string systemPrompt, string userPrompt,
        int maximumTokens = 800, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payload = new { model, temperature = 0, max_tokens = maximumTokens, stream = true,
            messages = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userPrompt } } };
        var json = JsonSerializer.Serialize(payload);
        var requestId = Guid.NewGuid().ToString("N");
        using var response = await SendWithRetryAsync(() => CreateRequest(json, requestId), requestId, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"AI 流式请求失败（{(int)response.StatusCode}）：{(string.IsNullOrWhiteSpace(detail) ? response.ReasonPhrase : detail)}");
        }
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

    private HttpRequestMessage CreateRequest(string json, string requestId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.TryAddWithoutValidation("X-Picer-Request-Id", requestId);
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> requestFactory, string requestId, CancellationToken cancellationToken)
    {
        // Remote IndexService/MediaAgent can briefly reconnect. Retry only
        // transport and gateway failures; never retry client errors or a
        // cancelled request. The request content is buffered and reusable.
        for (var attempt = 0; ; attempt++)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                using var request = requestFactory();
                var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                var transient = IsTransient(response.StatusCode);
                AppLogger.Info($"AI transport response; requestId={requestId}; endpoint={EndpointLabel}; attempt={attempt + 1}; status={(int)response.StatusCode}; transient={transient}; elapsedMs={watch.ElapsedMilliseconds}");
                if (!transient || attempt >= 2) return response;
                response.Dispose();
            }
            catch (HttpRequestException exception) when (attempt < 2 && !cancellationToken.IsCancellationRequested)
            {
                // Includes intermittent TLS/socket failures while the remote
                // service is restarting. Preserve the final exception.
                AppLogger.Warn($"AI transport retry; requestId={requestId}; endpoint={EndpointLabel}; attempt={attempt + 1}; exception={exception.GetType().Name}; inner={(exception.InnerException?.GetType().Name ?? "none")}; elapsedMs={watch.ElapsedMilliseconds}");
            }
            catch (HttpRequestException exception) when (!cancellationToken.IsCancellationRequested)
            {
                AppLogger.Error($"AI transport failed permanently; requestId={requestId}; endpoint={EndpointLabel}; attempts={attempt + 1}; exception={exception.GetType().Name}; inner={(exception.InnerException?.GetType().Name ?? "none")}", exception);
                throw;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < 2)
            {
                AppLogger.Warn($"AI transport timeout retry; requestId={requestId}; endpoint={EndpointLabel}; attempt={attempt + 1}; elapsedMs={watch.ElapsedMilliseconds}");
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                AppLogger.Error($"AI transport timeout permanently; requestId={requestId}; endpoint={EndpointLabel}; attempts={attempt + 1}", exception);
                throw;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private string EndpointLabel => $"{endpoint.Host}{endpoint.AbsolutePath}";

    public static string StripFence(string value)
    {
        var text = value.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;
        var first = text.IndexOf('\n'); var last = text.LastIndexOf("```", StringComparison.Ordinal);
        return first >= 0 && last > first ? text[(first + 1)..last].Trim() : text;
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
