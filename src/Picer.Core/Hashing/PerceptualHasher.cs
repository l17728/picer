using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Picer.Core.Hashing;

/// <summary>
/// Perceptual hash algorithms for image deduplication.
/// All hashes are stored as 64-bit integers (8 bytes = 64 bits).
/// </summary>
public static class PerceptualHasher
{
    /// <summary>
    /// Computes the average hash (aHash): resize to 8x8, convert to grayscale,
    /// set each bit based on whether pixel exceeds the mean luminance.
    /// Returns a 64-bit integer whose bits encode the hash.
    /// </summary>
    public static long ComputeAverageHash(Stream stream)
    {
        var pixels = new int[64]; // 8x8

        using var image = Image.Load<Rgba32>(stream);
        var tempImage = image.Clone(ctx => ctx.Resize(8, 8));

        long sum = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                var pixel = tempImage[x, y];
                // Convert RGBA to luminance (ITU-R BT.709 coefficients)
                int gray = (int)(0.2126f * pixel.R + 0.7152f * pixel.G + 0.0722f * pixel.B);
                pixels[y * 8 + x] = gray;
                sum += gray;
            }
        }

        int mean = (int)(sum / 64);
        long hash = 0;
        for (int i = 0; i < 64; i++)
        {
            if (pixels[i] > mean)
                hash |= 1L << i;
        }
        return hash;
    }

    /// <summary>
    /// Computes the difference hash (dHash): resize to 9x8, compare left/right columns.
    /// More sensitive to horizontal shifts than aHash.
    /// </summary>
    public static long ComputeDifferenceHash(Stream stream)
    {
        // Nine columns produce eight horizontal comparisons on each of eight
        // rows: 8 * 8 = 64 bits.
        var comparisons = new bool[64];

        using var image = Image.Load<Rgba32>(stream);
        var tempImage = image.Clone(ctx => ctx.Resize(9, 8));

        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                var left = GetGrayscale(tempImage[x, y]);
                var right = GetGrayscale(tempImage[x + 1, y]);
                comparisons[y * 8 + x] = left > right;
            }
        }

        long hash = 0;
        for (int i = 0; i < 64; i++)
        {
            if (comparisons[i])
                hash |= 1L << i;
        }
        return hash;
    }

    /// <summary>
    /// Returns the Hamming distance between two 64-bit hashes.
    /// Lower distance = more similar images.
    /// Max possible distance with our hashes: 64 bits.
    /// Typical threshold for near-duplicates: 10-20 (for aHash), 15-25 (for dHash).
    /// </summary>
    public static int HammingDistance(long hashA, long hashB)
    {
        var xor = hashA ^ hashB;
        int count = 0;
        while (xor != 0)
        {
            count++;
            xor &= xor - 1;
        }
        return count;
    }

    private static float GetGrayscale(Rgba32 p)
    {
        return 0.2126f * p.R + 0.7152f * p.G + 0.0722f * p.B;
    }
}
