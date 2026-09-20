using System.Text;
using Picer.Core.People;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Picer.Core.Vision;

public sealed class RemoteFaceVectorBackfillService(
    VisionAnalysisRepository repository,
    FaceTrainingSubmissionService platform,
    Func<string, string, IReadOnlyList<(FaceVectorBackfillSource Source, float[] Embedding)>, Task<int>>? persist = null)
{
    public async Task<FaceVectorBackfillResult> RunAsync(string sourceModelVersion, string targetModelVersion,
        IProgress<FaceVectorBackfillProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var sources = repository.GetFaceSources(sourceModelVersion);
        var batchSize = int.TryParse(Environment.GetEnvironmentVariable("PICER_FACE_VECTOR_BATCH_SIZE"), out var configuredBatch)
            ? Math.Clamp(configuredBatch, 16, 128) : 64;
        var cropParallelism = int.TryParse(Environment.GetEnvironmentVariable("PICER_FACE_CROP_PARALLELISM"), out var configuredParallelism)
            ? Math.Clamp(configuredParallelism, 1, Environment.ProcessorCount) : Math.Max(1, Math.Min(4, Environment.ProcessorCount));
        var completed = 0; var failed = 0; var saved = 0;
        foreach (var batch in sources.Chunk(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A burst often contains several faces from the same original
            // image. Read each source file once per batch, while keeping the
            // cache bounded to the current batch.
            var imageBytes = batch.Select(source => source.ImagePath).Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(path => path, path => TryRead(path), StringComparer.OrdinalIgnoreCase);
            var encoded = new string?[batch.Length];
            Parallel.For(0, batch.Length, new ParallelOptions { MaxDegreeOfParallelism = cropParallelism, CancellationToken = cancellationToken },
                index => encoded[index] = EncodeCrop(batch[index], imageBytes[batch[index].ImagePath], cancellationToken));
            var valid = batch.Zip(encoded).Where(pair => pair.Second is not null).ToArray();
            var vectors = valid.Length == 0 ? [] : await platform.GenerateEmbeddingsAsync(targetModelVersion,
                valid.Select(pair => pair.Second!).ToArray(), cancellationToken).ConfigureAwait(false);
            var values = valid.Take(vectors.Count).Zip(vectors).Select(pair => (pair.First.First, pair.Second)).ToArray();
            saved += persist is null
                ? repository.SaveBackfilledEmbeddings(sourceModelVersion, targetModelVersion, values)
                : await persist(sourceModelVersion, targetModelVersion, values).ConfigureAwait(false);
            completed += batch.Length;
            failed += batch.Length - valid.Length + Math.Max(0, valid.Length - vectors.Count);
            progress?.Report(new FaceVectorBackfillProgress(targetModelVersion, completed, sources.Count, saved, 0, failed, 1));
        }
        return new(targetModelVersion, sources.Count, saved, 0, failed, 1, failed == 0);
    }

    public static string? EncodeCrop(FaceVectorBackfillSource source, CancellationToken cancellationToken)
        => EncodeCrop(source, TryRead(source.ImagePath), cancellationToken);

    private static byte[]? TryRead(string path)
    {
        try { return File.ReadAllBytes(path); } catch { return null; }
    }

    private static string? EncodeCrop(FaceVectorBackfillSource source, byte[]? bytes, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes is null || bytes.Length == 0) return null;
            using var image = Image.Load(bytes);
            var paddingX = source.Box.Width * .25f; var paddingY = source.Box.Height * .25f;
            var left = Math.Clamp(source.Box.X - paddingX, 0, 1); var top = Math.Clamp(source.Box.Y - paddingY, 0, 1);
            var right = Math.Clamp(source.Box.X + source.Box.Width + paddingX, 0, 1); var bottom = Math.Clamp(source.Box.Y + source.Box.Height + paddingY, 0, 1);
            var rectangle = new Rectangle(Math.Clamp((int)(left * image.Width), 0, image.Width - 1), Math.Clamp((int)(top * image.Height), 0, image.Height - 1),
                Math.Max(1, Math.Min(image.Width, (int)((right - left) * image.Width))), Math.Max(1, Math.Min(image.Height, (int)((bottom - top) * image.Height))));
            image.Mutate(context => context.Crop(rectangle).Resize(new ResizeOptions { Mode = ResizeMode.Crop, Size = new Size(112, 112) }));
            using var stream = new MemoryStream(); image.SaveAsJpeg(stream, new JpegEncoder { Quality = 88 });
            return Convert.ToBase64String(stream.ToArray());
        }
        catch { return null; }
    }
}
