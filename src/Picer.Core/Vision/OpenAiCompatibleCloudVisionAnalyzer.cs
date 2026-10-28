using Picer.Core.Imaging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using Picer.Core.Models;
using Picer.Core.Logging;

namespace Picer.Core.Vision;

public sealed record CloudVisionApiOptions(
    Uri Endpoint,
    string ApiKey,
    string Model,
    string Provider,
    int MaximumImageEdge = 1280,
    int MaximumOutputTokens = 500,
    string PromptVersion = "picer-photo-v2-concise",
    bool UseClientAuthentication = false);

public sealed class VisionStreamException(string code) : Exception("Vision stream failed: " + code)
{
    public string ErrorCode { get; } = code;
}

public sealed class OpenAiCompatibleCloudVisionAnalyzer : ICloudVisionAnalyzer, IDisposable
{
    private readonly CloudVisionApiOptions _options;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly Func<ImageRecord, CancellationToken, Task<Stream?>>? _imageStreamFactory;

    public OpenAiCompatibleCloudVisionAnalyzer(CloudVisionApiOptions options, HttpClient? client = null, bool disposeClient = false,
        Func<ImageRecord, CancellationToken, Task<Stream?>>? imageStreamFactory = null)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey) && !options.UseClientAuthentication)
            throw new ArgumentException("API key is required unless authentication is supplied by the HTTP client.", nameof(options));
        if (options.MaximumImageEdge is < 256 or > 4096) throw new ArgumentOutOfRangeException(nameof(options));
        _options = options;
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _ownsClient = client is null || disposeClient;
        _imageStreamFactory = imageStreamFactory;
    }

    public string Provider => _options.Provider;
    public string ModelVersion => _options.Model;
    public string PromptVersion => _options.PromptVersion;

    public async Task<CloudVisionResult> AnalyzeAsync(CloudVisionRequest request, CancellationToken cancellationToken)
    {
        var imageDataUrl = await CreateSanitizedImageDataUrlAsync(request.Image, cancellationToken).ConfigureAwait(false);
        AppLogger.Info($"Photo analysis image preprocessing completed; imageId={request.Image.Id}");
        var contextJson = JsonSerializer.Serialize(new
        {
            location = request.Location,
            local_concepts = request.LocalConcepts,
            additional_context = request.AdditionalContext
        }, PromptJsonOptions);
        var payload = new
        {
            model = _options.Model,
            temperature = 0,
            max_tokens = _options.MaximumOutputTokens,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = "分析照片并返回JSON。以下是本地可信上下文：\n" + contextJson },
                        new { type = "image_url", image_url = new { url = imageDataUrl } }
                    }
                }
            }
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        message.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        AppLogger.Info($"Photo analysis model request started; provider={_options.Provider}; model={_options.Model}");
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        AppLogger.Info($"Photo analysis model response received; status={(int)response.StatusCode}");
        response.EnsureSuccessStatusCode();
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var envelope = TryParseJson(responseText);
        var content = envelope is not null
            ? envelope.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
            : ExtractStreamContent(responseText);
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("The vision API returned empty content.");
        var result = JsonSerializer.Deserialize<CloudVisionResult>(StripMarkdownFence(content), JsonOptions);
        if (result is null) throw new InvalidDataException("The vision API returned invalid JSON.");
        var inputTokens = 0L; var outputTokens = 0L;
        if (envelope is not null && envelope.RootElement.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var input)) inputTokens = input.GetInt64();
            if (usage.TryGetProperty("completion_tokens", out var output)) outputTokens = output.GetInt64();
        }
        return result with { Usage = DomesticCloudVisionPricing.Calculate(_options.Provider, _options.Model, inputTokens, outputTokens) };
    }

    internal static string? ExtractResponseContent(string text)
    {
        using var envelope = TryParseJson(text);
        return envelope is not null
            ? envelope.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
            : ExtractStreamContent(text);
    }

    private static JsonDocument? TryParseJson(string text)
    {
        try { return JsonDocument.Parse(text); } catch (JsonException) { return null; }
    }

    private static string? ExtractStreamContent(string text)
    {
        var builder = new StringBuilder();
        var eventName = "";
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line[6..].Trim();
                continue;
            }
            var payload = line.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? line[5..].Trim() : line.Trim();
            if (payload is "[DONE]" or "") continue;
            try
            {
                using var chunk = JsonDocument.Parse(payload);
                var root = chunk.RootElement;
                if (eventName.Equals("error", StringComparison.OrdinalIgnoreCase) ||
                    root.TryGetProperty("error", out _))
                {
                    var code = TryGetSafeErrorCode(root);
                    throw new VisionStreamException(code);
                }
                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                {
                    eventName = "";
                    continue;
                }
                var choice = choices[0];
                if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var value)) builder.Append(value.GetString());
                else if (choice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var full)) builder.Append(full.GetString());
            }
            catch (VisionStreamException) { throw; }
            catch (JsonException) { }
            finally { eventName = ""; }
        }
        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string TryGetSafeErrorCode(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
        {
            var value = code.GetString();
            if (!string.IsNullOrWhiteSpace(value) && value.Length <= 64 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
                return value;
        }
        return "ai_proxy_failed";
    }

    private async Task<string> CreateSanitizedImageDataUrlAsync(ImageRecord imageRecord, CancellationToken cancellationToken)
    {
        try
        {
            await using var source = _imageStreamFactory is null
                ? File.OpenRead(imageRecord.Path)
                : await _imageStreamFactory(imageRecord, cancellationToken).ConfigureAwait(false)
                  ?? throw new IOException("photo_analysis_input_unavailable");
            // HTTP response streams are deliberately non-seekable. ImageMagick's
            // stream decoder may seek while probing formats, so materialize bounded
            // remote inputs before decoding. The gateway caps them at 8 MiB.
            using var buffered = source.CanSeek ? null : new MemoryStream();
            Stream decodeSource = source;
            if (buffered is not null)
            {
                await source.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
                AppLogger.Info($"Photo analysis input buffered; imageId={imageRecord.Id}; bytes={buffered.Length}");
                buffered.Position = 0;
                decodeSource = buffered;
            }
            using var image = await RasterImage.LoadAsync(decodeSource, cancellationToken).ConfigureAwait(false);
            if (Math.Max(image.Width, image.Height) > _options.MaximumImageEdge)
            {
                var scale = (double)_options.MaximumImageEdge / Math.Max(image.Width, image.Height);
                image.Resize(Math.Max(1, (int)(image.Width * scale)),
                    Math.Max(1, (int)(image.Height * scale)));
            }
            image.StripMetadata();
            await using var output = new MemoryStream();
            await image.SaveAsJpegAsync(output, 82, cancellationToken).ConfigureAwait(false);
            return "data:image/jpeg;base64," + Convert.ToBase64String(output.ToArray());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLogger.Warn($"Photo analysis preprocessing failed; imageId={imageRecord.Id}; type={exception.GetType().Name}; inner={exception.InnerException?.GetType().Name ?? "none"}; hresult=0x{exception.HResult:X8}");
            throw;
        }
    }

    private static string StripMarkdownFence(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        var firstNewline = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline >= 0 && lastFence > firstNewline
            ? trimmed[(firstNewline + 1)..lastFence].Trim()
            : trimmed;
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions PromptJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private const string SystemPrompt = """
        你是私人照片库的结构化视觉分析器。只输出JSON，字段必须为：
        summary, scenes, activities, objects, searchTerms, locationDisplayName, locationSource。
        scenes、activities、objects、searchTerms必须是字符串数组。
        summary必须是言简意赅、自然通顺的一句中文，只写画面的主要信息；按画面实际可见内容优先包含时间段、地点、人物、行为、神态或状态、背景环境，不要逐项罗列，不要重复，不要使用“这张照片展示了”等套话。
        confirmedPeople是用户已确认的人物标签：存在时必须原样使用这些姓名，不得改名或推断关系；不存在时不得猜测姓名、身份或亲属关系，应使用可见的中性外观描述，例如“一个穿粉色连衣裙、戴遮阳帽的女孩”。
        只有上下文明确提供且与画面不冲突时才能写具体时间和地点；不得仅凭画面猜测精确地点。
        location只来自提供的GPS逆地理编码上下文；没有location时locationSource为none。
        附近地标必须使用“附近/周边”等措辞，不能声称照片一定在地标内部。
        本地标签只是候选线索，应由画面核验。描述保持客观、简洁、适合中文检索。
        """;
}
