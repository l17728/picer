using System.Collections.Immutable;
using Picer.Core.Data;
using Picer.Core.Hashing;
using Picer.Core.Models;

namespace Picer.Core.Search;

/// <summary>
/// Detects near-duplicate images using perceptual hash similarity.
/// Groups images whose aHash or dHash distance is below configurable thresholds.
/// </summary>
public static class DuplicateDetector
{
    public static ImmutableArray<ImageGroup> FindReviewGroups(
        PicerDatabase db,
        long? sourceId = null,
        int aHashThreshold = DefaultAHashThreshold,
        int dHashThreshold = DefaultDHashThreshold)
    {
        return FindReviewGroups(db.GetImages(sourceId, includeMissing: false), aHashThreshold, dHashThreshold);
    }

    public static ImmutableArray<ImageGroup> FindReviewGroups(
        IReadOnlyList<ImageRecord> images, int aHashThreshold = DefaultAHashThreshold,
        int dHashThreshold = DefaultDHashThreshold)
    {
        var exact = images
            .Where(image => !string.IsNullOrWhiteSpace(image.Sha256))
            .GroupBy(image => image.Sha256!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => new ImageGroup(group.OrderBy(image => image.Path), DuplicateGroupKind.ExactDuplicate))
            .ToList();
        var exactIds = exact.SelectMany(group => group.Images).Select(image => image.Id).ToHashSet();
        var visual = FindVisualDuplicates(images.Where(image => !exactIds.Contains(image.Id)), aHashThreshold, dHashThreshold);
        return exact.Concat(visual).ToImmutableArray();
    }

    /// <summary>
    /// Default threshold for average hash (aHash) similarity.
    /// Lower = stricter matching. Typical: 10-20 bits out of 64.
    /// </summary>
    public const int DefaultAHashThreshold = 15;

    /// <summary>
    /// Default threshold for difference hash (dHash) similarity.
    /// Lower = stricter matching. Typical: 15-25 bits out of 56.
    /// </summary>
    public const int DefaultDHashThreshold = 20;

    /// <summary>
    /// Finds duplicate groups among the given image records.
    /// Returns groups where each group contains similar images.
    /// A single image can only belong to one group (first-come-first-served).
    /// </summary>
    public static ImmutableArray<ImageGroup> FindDuplicates(
        PicerDatabase db,
        long? sourceId = null,
        int? minSize = 2,
        int aHashThreshold = DefaultAHashThreshold,
        int dHashThreshold = DefaultDHashThreshold)
    {
        var allImages = db.GetImages(sourceId, includeMissing: false);
        return FindVisualDuplicates(allImages, aHashThreshold, dHashThreshold, minSize).ToImmutableArray();
    }

    private static IEnumerable<ImageGroup> FindVisualDuplicates(
        IEnumerable<ImageRecord> images,
        int aHashThreshold,
        int dHashThreshold,
        int? minSize = 2)
    {
        // Only consider images that have been computed with phash (ahash != 0)
        var candidates = images.Where(i => i.AverageHash != 0).ToList();
        if (candidates.Count < 2)
            return [];

        // Sort by path for deterministic grouping
        candidates.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

        var used = new HashSet<long>();
        var groups = new List<ImageGroup>();

        for (int i = 0; i < candidates.Count && i < candidates.Count - 1; i++)
        {
            if (used.Contains(candidates[i].Id))
                continue;

            var group = new List<ImageRecord> { candidates[i] };
            used.Add(candidates[i].Id);

            for (int j = i + 1; j < candidates.Count; j++)
            {
                if (used.Contains(candidates[j].Id))
                    continue;

                if (IsSimilar(candidates[i], candidates[j], aHashThreshold, dHashThreshold))
                {
                    group.Add(candidates[j]);
                    used.Add(candidates[j].Id);
                }
            }

            if (group.Count >= (minSize ?? 2))
                groups.Add(new ImageGroup(group));
        }

        return groups;
    }

    private static bool IsSimilar(ImageRecord a, ImageRecord b, int aHashThresh, int dHashThresh)
    {
        // Check either hash independently — if either is similar enough, treat as duplicate
        var ahDist = PerceptualHasher.HammingDistance(a.AverageHash, b.AverageHash);
        var dhDist = PerceptualHasher.HammingDistance(a.DifferenceHash, b.DifferenceHash);
        return ahDist <= aHashThresh || dhDist <= dHashThresh;
    }
}
