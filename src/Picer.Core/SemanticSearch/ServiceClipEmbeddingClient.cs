using System.Net.Http.Json;
using Picer.Core.Models;

namespace Picer.Core.SemanticSearch;

public sealed record ClipEmbeddingResponse(string ModelVersion, float[] Embedding);

public sealed class ServiceClipEmbeddingClient(HttpClient client) : IClipEmbeddingClient, IDisposable
{
    public string ModelVersion { get; private set; } = ChineseClipEncoder.ModelVersion;

    public async Task<float[]> EncodeTextAsync(string text, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync("text", new { text }, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<float[]> EncodeImageAsync(ImageRecord image, CancellationToken cancellationToken)
    {
        var hash = NormalizeHash(image.ContentSha256) ?? NormalizeHash(image.Sha256)
            ?? throw new InvalidOperationException("clip_image_requires_verified_hash");
        using var response = await client.PostAsJsonAsync("image", new { contentSha256 = hash }, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<float[]> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ClipEmbeddingResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("clip_embedding_response_invalid");
        if (result.Embedding.Length is < 1 or > 4096 || result.Embedding.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("clip_embedding_vector_invalid");
        ModelVersion = result.ModelVersion;
        return result.Embedding;
    }

    private static string? NormalizeHash(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit) ? value.ToLowerInvariant() : null;
    public void Dispose() => client.Dispose();
}
