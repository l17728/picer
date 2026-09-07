using Picer.Core.Models;

namespace Picer.Core.Vision;

public sealed record FaceDetectionFallbackPolicy(int LowFaceCount = 1, float SmallFaceArea = .012f,
    float LargeImageEdge = 2200f)
{
    public bool ShouldRun(ImageRecord image, FaceAnalysisResult result) =>
        result.Faces.Count <= LowFaceCount || result.Faces.Any(x => x.Box.Width * x.Box.Height < SmallFaceArea) ||
        Math.Max(image.Width, image.Height) >= LargeImageEdge;
}

/// <summary>Runs a fast analyzer first and an optional higher-resolution fallback only when warranted.</summary>
public sealed class AdaptiveFaceAnalyzer(IImageFaceAnalyzer primary, IImageFaceAnalyzer fallback,
    FaceDetectionFallbackPolicy? policy = null) : IImageFaceAnalyzer
{
    private readonly FaceDetectionFallbackPolicy _policy = policy ?? new();
    public string AnalyzerName => primary.AnalyzerName;
    public string ModelVersion => $"adaptive:{primary.ModelVersion}+fallback:{fallback.ModelVersion}";

    public async Task<FaceAnalysisResult> AnalyzeAsync(ImageRecord image, CancellationToken cancellationToken)
    {
        var first = await primary.AnalyzeAsync(image, cancellationToken).ConfigureAwait(false);
        if (!_policy.ShouldRun(image, first)) return first;
        var second = await fallback.AnalyzeAsync(image, cancellationToken).ConfigureAwait(false);
        return new(Merge(first.Faces, second.Faces));
    }

    private static IReadOnlyList<FaceDetection> Merge(IReadOnlyList<FaceDetection> first, IReadOnlyList<FaceDetection> second)
    {
        var merged = first.OrderByDescending(x => x.Confidence).ToList();
        foreach (var candidate in second.OrderByDescending(x => x.Confidence))
        {
            if (merged.Any(existing => IoU(existing.Box, candidate.Box) >= .6f || SameEmbedding(existing.Embedding, candidate.Embedding))) continue;
            merged.Add(candidate);
        }
        return merged.OrderBy(x => x.Box.Y).ThenBy(x => x.Box.X).ToArray();
    }

    private static float IoU(NormalizedFaceBox a, NormalizedFaceBox b)
    {
        var left = Math.Max(a.X, b.X); var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.Width, b.X + b.Width); var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        var intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        var union = a.Width * a.Height + b.Width * b.Height - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private static bool SameEmbedding(IReadOnlyList<float>? a, IReadOnlyList<float>? b)
    {
        if (a is null || b is null || a.Count != b.Count || a.Count == 0) return false;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Count; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na > 0 && nb > 0 && dot / Math.Sqrt(na * nb) >= .95;
    }

    public void Dispose()
    {
        (primary as IDisposable)?.Dispose();
        (fallback as IDisposable)?.Dispose();
    }
}
