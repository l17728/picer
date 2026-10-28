namespace Picer.Core.Vision;

/// <summary>Summarizes representative text through the authenticated index-service AI proxy.</summary>
public sealed class ServiceSceneGroupSummarizer : ISceneGroupSummarizer
{
    private readonly OpenAiCompatibleSceneSummarizer _inner;

    public ServiceSceneGroupSummarizer(Uri serviceEndpoint, string bearerToken, string model,
        HttpClient? client = null, bool disposeClient = false)
    {
        var endpoint = new Uri(new Uri(serviceEndpoint.AbsoluteUri.TrimEnd('/') + "/"), "api/ai/v1/chat/stream");
        _inner = new OpenAiCompatibleSceneSummarizer(
            new CloudVisionApiOptions(endpoint, bearerToken, model, "service"), client, disposeClient);
    }

    public string Provider => _inner.Provider;
    public string ModelVersion => _inner.ModelVersion;
    public string PromptVersion => _inner.PromptVersion;

    public Task<SceneSummaryResult> SummarizeAsync(IReadOnlyList<SceneRepresentativeDescription> descriptions,
        CancellationToken cancellationToken = default) => _inner.SummarizeAsync(descriptions, cancellationToken);

    public void Dispose() => _inner.Dispose();
}
