using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.Core.SemanticSearch;

public sealed class ClipSemanticService(PicerDatabase db, ClipSemanticRepository repository, ChineseClipEncoder encoder)
{
    public string ModelVersion => ChineseClipEncoder.ModelVersion;
    public async Task BuildIndexAsync(IProgress<ClipIndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var images = db.GetImages(includeMissing: false).Where(x => x.Status == ImageStatus.Active && File.Exists(x.Path)).ToArray();
        var completed = 0;
        foreach (var image in images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!repository.IsCurrent(image, ModelVersion))
            {
                var vector = await Task.Run(() => encoder.EncodeImage(image.Path), cancellationToken).ConfigureAwait(false);
                repository.Upsert(image, ModelVersion, vector);
            }
            progress?.Report(new(++completed, images.Length, image.Path));
        }
    }
    public Task<IReadOnlyList<ClipSearchHit>> SearchAsync(string query, int count = 20, CancellationToken cancellationToken = default) =>
        Task.Run(() => repository.Search(encoder.EncodeText(query), count), cancellationToken);
}
