using Picer.Core.Models;
using Picer.Core.Hashing;

namespace Picer.Core.Search;

public enum DuplicateGroupKind { ExactDuplicate, VisualSimilarity }

/// <summary>
/// Represents a group of near-duplicate images detected by perceptual hash comparison.
/// </summary>
public sealed class ImageGroup
{
    public List<ImageRecord> Images { get; }
    public DuplicateGroupKind Kind { get; }
    public string Algorithm => Kind == DuplicateGroupKind.ExactDuplicate ? "SHA-256" : "aHash / dHash";

    /// <summary>
    /// The representative image for this group (typically the first/most complete record).
    /// </summary>
    public ImageRecord Representative => Images[0];

    public int DuplicateCount => Images.Count - 1;

    /// <summary>
    /// Approximate wasted space by removing all but one image in this group.
    /// Only estimates based on file sizes (does not account for identical exact duplicates which are already handled by SHA-256 dedup).
    /// </summary>
    public long EstimatedWastedSizeBytes
    {
        get
        {
            if (Images.Count < 2)
                return 0;
            var repSize = Representative.FileSize;
            // Subtract the representative's own size — only count duplicates as waste
            return Images.Skip(1).Sum(img => img.FileSize);
        }
    }

    public ImageGroup(IEnumerable<ImageRecord> records, DuplicateGroupKind kind = DuplicateGroupKind.VisualSimilarity)
    {
        Images = new List<ImageRecord>(records);
        Kind = kind;
    }

    public int SimilarityPercent(ImageRecord image)
    {
        if (Kind == DuplicateGroupKind.ExactDuplicate) return 100;
        var ah = PerceptualHasher.HammingDistance(Representative.AverageHash, image.AverageHash);
        var dh = PerceptualHasher.HammingDistance(Representative.DifferenceHash, image.DifferenceHash);
        return (int)Math.Round(Math.Max(1d - ah / 64d, 1d - dh / 56d) * 100);
    }
}
