using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Picer.Core.Vision;

public sealed record CloudVisionApiOptions(
    Uri Endpoint,
    string ApiKey,
    string Model,
    string Provider,
    int MaximumImageEdge = 1280,
    int MaximumOutputTokens = 500,
    string PromptVersion = "picer-photo-v2-concise");

public sealed class OpenAiCompatibleCloudVisionAnalyzer : ICloudVisionAnalyzer, IDisposable
{
    private readonly CloudVisionApiOptions _options;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public OpenAiCompatibleCloudVisionAnalyzer(CloudVisionApiOptions options, HttpClient? client = null)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey)) throw new ArgumentException("API key is required.", nameof(options));
        if (options.MaximumImageEdge is < 256 or > 4096) throw new ArgumentOutOfRangeException(nameof(options));
        _options = options;
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _ownsClient = client is null;
    }

    public string Provider => _options.Provider;
    public string ModelVersion => _options.Model;
    public string PromptVersion => _options.PromptVersion;

    public async Task<CloudVisionResult> AnalyzeAsync(CloudVisionRequest request, CancellationToken cancellationToken)
    {
        var imageDataUrl = await CreateSanitizedImageDataUrlAsync(request.Image.Path, cancellationToken).ConfigureAwait(false);
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
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        message.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var envelope = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var content = envelope.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("The vision API returned empty content.");
        var result = JsonSerializer.Deserialize<CloudVisionResult>(StripMarkdownFence(content), JsonOptions);
        if (result is null) throw new InvalidDataException("The vision API returned invalid JSON.");
        var inputTokens = 0L; var outputTokens = 0L;
        if (envelope.RootElement.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var input)) inputTokens = input.GetInt64();
            if (usage.TryGetProperty("completion_tokens", out var output)) outputTokens = output.GetInt64();
        }
        return result with { Usage = DomesticCloudVisionPricing.Calculate(_options.Provider, _options.Model, inputTokens, outputTokens) };
    }

    private async Task<string> CreateSanitizedImageDataUrlAsync(string path, CancellationToken cancellationToken)
    {
        await using var source = File.OpenRead(path);
        using var image = await Image.LoadAsync(source, cancellationToken).ConfigureAwait(false);
        if (Math.Max(image.Width, image.Height) > _options.MaximumImageEdge)
        {
            var scale = (double)_options.MaximumImageEdge / Math.Max(image.Width, image.Height);
            image.Mutate(operation => operation.Resize(Math.Max(1, (int)(image.Width * scale)),
                Math.Max(1, (int)(image.Height * scale))));
        }
        image.Metadata.ExifProfile = null;
        image.Metadata.IccProfile = null;
        image.Metadata.XmpProfile = null;
        await using var output = new MemoryStream();
        await image.SaveAsJpegAsync(output, new JpegEncoder { Quality = 82 }, cancellationToken).ConfigureAwait(false);
        return "data:image/jpeg;base64," + Convert.ToBase64String(output.ToArray());
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
