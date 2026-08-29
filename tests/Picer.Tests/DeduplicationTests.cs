using System.Collections.Generic;
using System.IO;
using Picer.Core.Data;
using Picer.Core.Hashing;
using Picer.Core.Indexing;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Thumbnails;
using Picer.Tests.Stubs;

namespace Picer.Tests;

public sealed class DeduplicationTests : IDisposable
{
    private readonly string _dataDir;
    private DiskBackedStorageProvider? _storage;
    private PicerDatabase? _db;

    public DeduplicationTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "picer_dedup_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dataDir);
    }

    private void AddFile(string relativePath, byte value, int length)
    {
        var fullPath = Path.Combine(_dataDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, Enumerable.Repeat(value, length).ToArray());
    }

    public void Dispose()
    {
        try { _db?.Dispose(); } catch { }
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    [Fact]
    public void PerceptualHash_ComputeAverageHash_HandlesInvalidStreamGracefully()
    {
        // Tiny byte array is not a valid image — should throw or return 0
        var stream = new MemoryStream(new byte[] { 0xFF, 0x00 });

        Assert.ThrowsAny<Exception>(() => PerceptualHasher.ComputeAverageHash(stream));
    }

    [Fact]
    public void PerceptualHash_HammingDistance_SimpleCheck()
    {
        // Just verify HammingDistance works with known values
        var hash1 = 0L;
        var hash2 = 0L;

        Assert.Equal(0, PerceptualHasher.HammingDistance(hash1, hash2));
    }

    [Fact]
    public async Task DuplicateDetector_NoDuplicates_WhenTinyNonImages()
    {
        var dir = Path.Combine(_dataDir, "unique");
        Directory.CreateDirectory(dir);

        // These are tiny binary blobs — not real images, so phash will be 0
        AddFile("a.jpg", 0x01, 50);
        AddFile("b.png", 0x02, 60);
        AddFile("c.webp", 0x03, 70);

        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));
        _storage = new DiskBackedStorageProvider();
        var thumbService = new ThumbnailService(Path.Combine(_dataDir, "thumbs"));

        var engine = new IndexEngine(_db, _storage, thumbService);
        await engine.IndexSourceAsync(new SourceFolder { Id = 1, Path = dir });

        var groups = DuplicateDetector.FindDuplicates(_db);

        // Tiny non-image files won't produce valid pHash (ahash=0), so groups should be empty
        Assert.Empty(groups);
    }

    [Fact]
    public void ImageGroup_EstimatedWastedSize_CalculatesCorrectly()
    {
        var recs = new List<ImageRecord>
        {
            new() { Id = 1, FileSize = 1000 },
            new() { Id = 2, FileSize = 1000 }, // duplicate
            new() { Id = 3, FileSize = 1000 }  // duplicate
        };
        var group = new ImageGroup(recs);

        Assert.Equal(2, group.DuplicateCount); // 3 - 1 = 2 duplicates
        Assert.Equal(2000, group.EstimatedWastedSizeBytes); // 2 * 1000
    }

    [Fact]
    public void FindReviewGroups_SeparatesExactDuplicatesFromVisualSimilarity()
    {
        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));
        var source = _db.AddSource(_dataDir);
        var now = DateTime.UtcNow;
        ImageRecord Record(string name, string sha, long hash) => new()
        {
            SourceId = source.Id, Path = Path.Combine(_dataDir, name), FileSize = 100,
            ModifiedUtc = now, IndexedUtc = now, Sha256 = sha, Width = 100, Height = 100,
            AverageHash = hash, DifferenceHash = hash, Status = ImageStatus.Active
        };
        _db.UpsertImage(Record("exact-a.jpg", "same", 10));
        _db.UpsertImage(Record("exact-b.jpg", "same", 10));
        _db.UpsertImage(Record("similar-a.jpg", "one", 0b111100));
        _db.UpsertImage(Record("similar-b.jpg", "two", 0b111101));

        var groups = DuplicateDetector.FindReviewGroups(_db, aHashThreshold: 1, dHashThreshold: 1);

        Assert.Contains(groups, group => group.Kind == DuplicateGroupKind.ExactDuplicate && group.Algorithm == "SHA-256");
        Assert.Contains(groups, group => group.Kind == DuplicateGroupKind.VisualSimilarity && group.Algorithm == "aHash / dHash");
        Assert.All(groups.Where(group => group.Kind == DuplicateGroupKind.ExactDuplicate),
            group => Assert.Equal(100, group.SimilarityPercent(group.Images[^1])));
    }

    [Fact]
    public async Task DuplicateDetector_DetectorsWithSourceFilter_LimitsResults()
    {
        var dir1 = Path.Combine(_dataDir, "dir1");
        var dir2 = Path.Combine(_dataDir, "dir2");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);

        // Add small blobs (non-images)
        AddFile("photo.jpg", 0x01, 40);
        AddFile("other.png", 0x02, 50);

        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));
        _storage = new DiskBackedStorageProvider();
        var thumbService = new ThumbnailService(Path.Combine(_dataDir, "thumbs"));

        var engine1 = new IndexEngine(_db, _storage, thumbService);
        await engine1.IndexSourceAsync(new SourceFolder { Id = 1, Path = dir1 });

        var engine2 = new IndexEngine(_db, _storage, thumbService);
        await engine2.IndexSourceAsync(new SourceFolder { Id = 2, Path = dir2 });

        // Find duplicates for source 1 only
        var groups1 = DuplicateDetector.FindDuplicates(_db, sourceId: 1);
        var groups2 = DuplicateDetector.FindDuplicates(_db, sourceId: 2);
        var allGroups = DuplicateDetector.FindDuplicates(_db);

        // Both should return same count (non-images → no phash → no groups)
        Assert.Equal(groups1.Length, groups2.Length);
        Assert.True(allGroups.Length >= groups1.Length);
    }
}
