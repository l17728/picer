using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Search;

namespace Picer.Core.SemanticSearch;

public interface IClipEmbeddingClient
{
    string ModelVersion { get; }
    Task<float[]> EncodeTextAsync(string text, CancellationToken cancellationToken);
    Task<float[]> EncodeImageAsync(ImageRecord image, CancellationToken cancellationToken);
}

public sealed class LocalClipEmbeddingClient(ChineseClipEncoder encoder) : IClipEmbeddingClient, IDisposable
{
    public string ModelVersion => ChineseClipEncoder.ModelVersion;
    public Task<float[]> EncodeTextAsync(string text, CancellationToken cancellationToken) =>
        Task.Run(() => encoder.EncodeText(text), cancellationToken);
    public Task<float[]> EncodeImageAsync(ImageRecord image, CancellationToken cancellationToken) =>
        Task.Run(() => encoder.EncodeImage(image.Path), cancellationToken);
    public void Dispose() => encoder.Dispose();
}

public sealed class ClipSemanticService(PicerDatabase db, ClipSemanticRepository repository, IClipEmbeddingClient embeddings) : IDisposable
{
    public ClipSemanticService(PicerDatabase db, ClipSemanticRepository repository, ChineseClipEncoder encoder)
        : this(db, repository, new LocalClipEmbeddingClient(encoder)) { }
    public string ModelVersion => embeddings.ModelVersion;
    public async Task BuildIndexAsync(IProgress<ClipIndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var images = db.GetImages(includeMissing: false).Where(x => x.Status == ImageStatus.Active && File.Exists(x.Path)).ToArray();
        var completed = 0;
        foreach (var image in images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!repository.IsCurrent(image, ModelVersion))
            {
                var vector = await embeddings.EncodeImageAsync(image, cancellationToken).ConfigureAwait(false);
                repository.Upsert(image, ModelVersion, vector);
            }
            progress?.Report(new(++completed, images.Length, image.Path));
        }
    }
    public Task<IReadOnlyList<ClipSearchHit>> SearchAsync(string query, int count = 20, CancellationToken cancellationToken = default) =>
        SearchCoreAsync(query, count, cancellationToken);

    private async Task<IReadOnlyList<ClipSearchHit>> SearchCoreAsync(string query, int count, CancellationToken cancellationToken)
    {
        var vector = await embeddings.EncodeTextAsync(query, cancellationToken).ConfigureAwait(false);
        return await Task.Run<IReadOnlyList<ClipSearchHit>>(() => repository.SearchStreaming(vector, count,
            ModelVersion, cancellationToken: cancellationToken).Hits, cancellationToken).ConfigureAwait(false);
    }

    public Task<HybridPhotoSearchResult> SearchHybridAsync(string query, SearchFilters? hardFilters = null,
        int count = 20, CancellationToken cancellationToken = default) =>
        new HybridPhotoSearchService(db, repository, embeddings).SearchAsync(query, hardFilters, count,
            cancellationToken: cancellationToken);

    public void Dispose() => (embeddings as IDisposable)?.Dispose();
}
