using System.IO;
using Picer.Core.Data;
using Picer.Core.Hashing;
using Picer.Core.Indexing;
using Picer.Core.Models;
using Picer.Core.Storage;
using Picer.Core.Thumbnails;
using Picer.Core.Vision;
using Picer.Tests.Stubs;

namespace Picer.Tests;

/// <summary>
/// Integration tests using real temp directory + DiskBackedStorageProvider
/// to cover scan, incremental update, missing detection, and relocation.
/// </summary>
public sealed class IndexEngineTests : IDisposable
{
    private readonly string _dataDir;
    private readonly string _dbPath;
    private readonly string _thumbDir;
    private DiskBackedStorageProvider? _storage;
    private PicerDatabase? _db;

    public IndexEngineTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "picer_et_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dataDir);
        _thumbDir = Path.Combine(_dataDir, "thumbs");
        _dbPath = Path.Combine(_dataDir, "test.db");
    }

    private IndexEngine CreateEngine()
    {
        var storage = new DiskBackedStorageProvider();
        _storage = storage;
        var db = new PicerDatabase(_dbPath);
        _db = db;
        return new IndexEngine(db, storage, new ThumbnailService(_thumbDir));
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
    public async Task IndexSource_FindsAndIndexesSupportedImages()
    {
        AddFile("photos/a.jpg", 0x41, 100);
        AddFile("photos/b.png", 0x42, 200);
        AddFile("photos/c.webp", 0x43, 300);
        AddFile("photos/notes.txt", 0x54, 50); // ignored

        var root = Path.GetFullPath(_dataDir);
        var engine = CreateEngine();
        var result = await engine.IndexSourceAsync(new SourceFolder { Id = 1, Path = root });

        Assert.Equal(3, result.Scanned);   // jpg/png/webp (txt excluded by format filter)
        Assert.Equal(3, result.Indexed);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.MarkedMissing);

        var images = _db!.GetImages(includeMissing: false);
        Assert.Equal(3, images.Count);
    }

    [Fact]
    public async Task IndexSource_DuplicatesAreSkipped_NextRun()
    {
        AddFile("photos/a.jpg", 0x41, 100);
        AddFile("photos/b.png", 0x42, 200);
        AddFile("photos/c.webp", 0x43, 300);

        var root = Path.GetFullPath(_dataDir);
        var engine = CreateEngine();
        var source = new SourceFolder { Id = 1, Path = root };

        await engine.IndexSourceAsync(source);

        var result = await engine.IndexSourceAsync(source);

        Assert.Equal(3, result.Scanned);
        Assert.Equal(0, result.Indexed);
        Assert.Equal(3, result.Skipped);
    }

    [Fact]
    public void BurstGroups_UseCaptureTimeAndVisualHashesAndRebuildIncrementally()
    {
        var engine = CreateEngine();
        var source = _db!.AddSource(_dataDir);
        var start = new DateTime(2026, 9, 3, 8, 0, 0, DateTimeKind.Utc);
        ImageRecord Add(string name, int seconds, long hash)
        {
            var path = Path.Combine(_dataDir, name);
            File.WriteAllBytes(path, [1, 2, 3]);
            return _db.UpsertImage(new ImageRecord
            {
                SourceId = source.Id, Path = path, FileSize = 3, ModifiedUtc = start.AddSeconds(seconds),
                TakenUtc = start.AddSeconds(seconds), Width = 1920, Height = 1080, Camera = "Camera A",
                AverageHash = hash, DifferenceHash = 0, Status = ImageStatus.Active, IndexedUtc = DateTime.UtcNow
            });
        }

        var first = Add("burst-1.jpg", 0, 0x1110);
        var second = Add("burst-2.jpg", 2, 0x1111);
        var separate = Add("separate.jpg", 10, 0x7777);
        var service = new BurstGroupService(_db);

        Assert.True(service.NeedsRebuild(source.Id));
        Assert.Equal(1, service.RebuildSource(source.Id));
        Assert.False(service.NeedsRebuild(source.Id));
        var group = Assert.Single(service.GetGroupsForImages([first.Id, second.Id, separate.Id]).Values);
        Assert.Equal([first.Id, second.Id], group.ImageIds);

        var third = Add("burst-3.jpg", 3, 0x1113);
        Assert.True(service.NeedsRebuild(source.Id));
        Assert.Equal(1, service.RebuildSource(source.Id));
        group = Assert.Single(service.GetGroupsForImages([first.Id, second.Id, third.Id]).Values);
        Assert.Equal([first.Id, second.Id, third.Id], group.ImageIds);
        var leakage = new VisionAnalysisRepository(_db).GetTrainingLeakageGroups([first.Id, second.Id, third.Id, separate.Id]);
        Assert.Equal(leakage[first.Id], leakage[second.Id]);
        Assert.Equal(leakage[first.Id], leakage[third.Id]);
        Assert.NotEqual(leakage[first.Id], leakage[separate.Id]);
    }

    [Fact]
    public async Task IndexSource_Incremental_ReindexesChangedFile()
    {
        AddFile("photos/a.jpg", 0x41, 100);
        AddFile("photos/b.png", 0x42, 200);
        AddFile("photos/c.webp", 0x43, 300);

        var root = Path.GetFullPath(_dataDir);
        var engine = CreateEngine();
        var source = new SourceFolder { Id = 1, Path = root };

        await engine.IndexSourceAsync(source);

        // Modify b.png content AND update its timestamp
        AddFile("photos/b.png", 0x58, 999);

        var result = await engine.IndexSourceAsync(source);

        Assert.Equal(1, result.Indexed);
        Assert.Equal(2, result.Skipped);
    }

    [Fact]
    public async Task IndexSource_NewFile_IsIndexedNextRun()
    {
        AddFile("photos/a.jpg", 0x41, 100);
        AddFile("photos/b.png", 0x42, 200);

        var root = Path.GetFullPath(_dataDir);
        var engine = CreateEngine();
        var source = new SourceFolder { Id = 1, Path = root };

        await engine.IndexSourceAsync(source);
        Assert.Equal(2, _db!.GetImages(1).Count);

        AddFile("photos/c.gif", 0x43, 400);
        AddFile("photos/d.tiff", 0x44, 500);

        var result = await engine.IndexSourceAsync(source);

        Assert.Equal(4, result.Scanned);
        Assert.Equal(2, result.Indexed);
        Assert.Equal(2, result.Skipped);
    }

    [Fact]
    public async Task VerifyAsync_DetectsRemovedFiles()
    {
        AddFile("photos/a.jpg", 0x41, 100);
        AddFile("photos/b.png", 0x42, 200);
        AddFile("photos/c.webp", 0x43, 300);

        var root = Path.GetFullPath(_dataDir);
        var engine = CreateEngine();
        var source = new SourceFolder { Id = 1, Path = root };

        await engine.IndexSourceAsync(source);
        var allBefore = _db!.GetImages(1);
        Assert.All(allBefore, r => Assert.Equal(ImageStatus.Active, r.Status));

        // Remove c.webp physically
        File.Delete(Path.Combine(_dataDir, "photos", "c.webp"));

        var missing = await engine.VerifyAsync();

        Assert.Single(missing);
        Assert.Contains("c.webp", missing[0].Path);
        Assert.Equal(ImageStatus.Missing, missing[0].Status);
    }

    [Fact]
    public async Task RelocateAsync_MovesMissingFileToNewLocation()
    {
        AddFile("photos/a.jpg", 0x41, 100);
        AddFile("photos/b.png", 0x42, 200);

        var root = Path.GetFullPath(_dataDir);
        var engine = CreateEngine();
        var source = new SourceFolder { Id = 1, Path = root };

        await engine.IndexSourceAsync(source);
        var original = _db!.GetImages(1).Single(r => r.Path.EndsWith("b.png"));
        var originalId = original.Id;
        var originalSha = original.Sha256;

        // Move b.png to backup/renamed_b.png (same content → same sha)
        var oldPath = Path.Combine(_dataDir, "photos", "b.png");
        var newPath = Path.Combine(_dataDir, "backup", "renamed_b.png");
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        File.Move(oldPath, newPath);
        // Also delete from photos dir so Verify sees it as missing at old location
        File.Delete(oldPath);

        var relocateResult = await engine.RelocateAsync(originalId, newPath);
        var relocated = relocateResult.Image;

        Assert.True(relocateResult.Success);

        Assert.NotNull(relocated);
        Assert.Equal(originalId, relocated.Id);
        Assert.Contains("backup", relocated.Path);
        Assert.Equal(originalSha, relocated.Sha256);
        Assert.Equal(ImageStatus.Active, relocated.Status);
    }

    [Fact]
    public async Task RelocateAsync_ReturnsNull_ForUnsupportedExtension()
    {
        AddFile("photos/a.jpg", 0x41, 100);

        var root = Path.GetFullPath(_dataDir);
        var engine = CreateEngine();
        var source = new SourceFolder { Id = 1, Path = root };

        await engine.IndexSourceAsync(source);
        var aRecord = _db!.GetImages(1).First(r => r.Path.EndsWith("a.jpg"));

        var fakePath = Path.Combine(_dataDir, "photos", "placeholder.xyz");
        File.WriteAllText(fakePath, "not an image");

        var result = await engine.RelocateAsync(aRecord.Id, fakePath);
        Assert.False(result.Success);
        Assert.Equal(RelocateFailure.UnsupportedOrMissingFile, result.Failure);
    }

    [Fact]
    public async Task BatchTransaction_AllFilesCommittedAtomically()
    {
        for (int i = 0; i < 20; i++)
            AddFile($"photos/img_{i:D2}.jpg", (byte)i, 100 + i);

        var root = Path.GetFullPath(_dataDir);
        var engine = CreateEngine();
        var result = await engine.IndexSourceAsync(new SourceFolder { Id = 1, Path = root });

        Assert.Equal(20, result.Scanned);
        Assert.Equal(20, result.Indexed);
        Assert.Equal(20, _db!.GetImages(1).Count);
    }

    [Fact]
    public async Task BatchTransaction_IncrementalSkipsUnchangedFiles()
    {
        for (int i = 0; i < 10; i++)
            AddFile($"photos/img_{i:D2}.jpg", (byte)i, 100 + i);

        var root = Path.GetFullPath(_dataDir);
        var engine = CreateEngine();
        var source = new SourceFolder { Id = 1, Path = root };

        await engine.IndexSourceAsync(source);
        var first = await engine.IndexSourceAsync(source);

        Assert.Equal(10, first.Scanned);
        Assert.Equal(0, first.Indexed);
        Assert.Equal(10, first.Skipped);
    }

    [Fact]
    public async Task BatchTransaction_PartialUpdate_OnlyChangedFilesReindexed()
    {
        for (int i = 0; i < 10; i++)
            AddFile($"photos/img_{i:D2}.jpg", (byte)i, 100 + i);

        var root = Path.GetFullPath(_dataDir);
        var engine = CreateEngine();
        var source = new SourceFolder { Id = 1, Path = root };

        await engine.IndexSourceAsync(source);

        AddFile("photos/img_05.jpg", 0xFF, 999);
        AddFile("photos/img_07.jpg", 0xFE, 888);

        var result = await engine.IndexSourceAsync(source);

        Assert.Equal(10, result.Scanned);
        Assert.Equal(2, result.Indexed);
        Assert.Equal(8, result.Skipped);
    }

    [Fact]
    public async Task RemoveIndexRecordAsync_DeletesDatabaseRecordAndThumbnailButPreservesSource()
    {
        AddFile("photos/a.jpg", 0x41, 100);

        var root = Path.GetFullPath(_dataDir);
        var engine = CreateEngine();
        await engine.IndexSourceAsync(new SourceFolder { Id = 1, Path = root });

        var rec = _db!.GetImages(1).Single();
        var thumbPath = _thumbDir + $"/{rec.Id}.jpg";
        File.WriteAllText(thumbPath, "fake-thumb");

        await engine.RemoveIndexRecordAsync(rec.Id);

        Assert.Null(_db.GetImage(rec.Id));
        Assert.False(File.Exists(thumbPath), "Thumbnail file should be deleted after removing the index record");
        Assert.True(File.Exists(rec.Path), "Removing an index record must never delete the source image");
    }

    [Fact]
    public async Task RemoveIndexRecordAsync_OnlyDeletesSpecifiedImageRecord()
    {
        AddFile("photos/a.jpg", 0x41, 100);
        AddFile("photos/b.png", 0x42, 200);

        var root = Path.GetFullPath(_dataDir);
        var engine = CreateEngine();
        await engine.IndexSourceAsync(new SourceFolder { Id = 1, Path = root });

        var images = _db!.GetImages(1);
        var recA = images.Single(r => r.Path.EndsWith("a.jpg"));
        var recB = images.Single(r => r.Path.EndsWith("b.png"));

        await engine.RemoveIndexRecordAsync(recA.Id);

        Assert.Null(_db.GetImage(recA.Id));
        Assert.NotNull(_db.GetImage(recB.Id));
        Assert.Equal(ImageStatus.Active, _db.GetImage(recB.Id)!.Status);
    }

    [Fact]
    public async Task MultipleSources_IndexingIsolation()
    {
        var dir1 = Path.Combine(_dataDir, "src1");
        var dir2 = Path.Combine(_dataDir, "src2");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);

        File.WriteAllBytes(Path.Combine(dir1, "a.jpg"), Enumerable.Repeat((byte)0x41, 100).ToArray());
        File.WriteAllBytes(Path.Combine(dir2, "b.jpg"), Enumerable.Repeat((byte)0x42, 200).ToArray());

        var engine = CreateEngine();
        await engine.IndexSourceAsync(new SourceFolder { Id = 1, Path = Path.GetFullPath(dir1) });
        await engine.IndexSourceAsync(new SourceFolder { Id = 2, Path = Path.GetFullPath(dir2) });

        var src1Images = _db!.GetImages(1);
        var src2Images = _db.GetImages(2);

        Assert.Single(src1Images);
        Assert.Contains("a.jpg", src1Images[0].Path);
        Assert.Single(src2Images);
        Assert.Contains("b.jpg", src2Images[0].Path);

        var allSources = _db.GetSources();
        Assert.Equal(2, allSources.Count);
    }

    [Fact]
    public async Task IndexSource_LargeBatch_HandlesManyFiles()
    {
        for (int i = 0; i < 100; i++)
            AddFile($"batch/img_{i:D3}.jpg", (byte)(i % 256), 50 + (i % 100));

        var root = Path.GetFullPath(_dataDir);
        var engine = CreateEngine();
        var result = await engine.IndexSourceAsync(new SourceFolder { Id = 1, Path = root });

        Assert.Equal(100, result.Scanned);
        Assert.Equal(100, result.Indexed);
        Assert.Equal(100, _db!.GetImages(1).Count);
    }

    [Fact]
    public async Task IndexSource_NewFilesAddedBetweenRuns_AreIndexed()
    {
        AddFile("photos/a.jpg", 0x41, 100);

        var root = Path.GetFullPath(_dataDir);
        var engine = CreateEngine();
        var source = new SourceFolder { Id = 1, Path = root };

        await engine.IndexSourceAsync(source);
        Assert.Single(_db!.GetImages(1));

        for (int i = 0; i < 5; i++)
            AddFile($"photos/new_{i}.png", (byte)(0x50 + i), 100 + i);

        var result = await engine.IndexSourceAsync(source);

        Assert.Equal(6, result.Scanned);
        Assert.Equal(5, result.Indexed);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public async Task IndexSource_CancellationStopsBoundedPipelinePromptly()
    {
        _db = new PicerDatabase(_dbPath);
        var storage = new DelayedOpenStorageProvider(_dataDir, 50);
        var engine = new IndexEngine(_db, storage, new ThumbnailService(_thumbDir));
        using var cancellation = new CancellationTokenSource();
        var running = engine.IndexSourceAsync(new SourceFolder { Path = _dataDir }, ct: cancellation.Token);
        await Task.Delay(50);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"取消耗时 {watch.Elapsed}");
    }

    [Fact]
    public async Task RelocateAsync_RejectsDifferentContentWithSameExtension()
    {
        AddFile("photos/original.jpg", 0x41, 100);
        AddFile("other/different.jpg", 0x42, 100);

        var engine = CreateEngine();
        await engine.IndexSourceAsync(new SourceFolder { Id = 1, Path = Path.GetFullPath(_dataDir) });
        var original = _db!.GetImages(1).Single(r => r.Path.EndsWith("original.jpg"));
        var originalPath = original.Path;

        var result = await engine.RelocateAsync(original.Id, Path.Combine(_dataDir, "other", "different.jpg"));

        Assert.False(result.Success);
        Assert.Equal(RelocateFailure.HashMismatch, result.Failure);
        Assert.Equal(originalPath, _db.GetImage(original.Id)!.Path);
    }

    [Fact]
    public async Task BuildRelocationPlanAsync_PreviewsHashMatchesWithoutChangingDatabase()
    {
        AddFile("photos/moved.jpg", 0x7A, 128);
        var engine = CreateEngine();
        await engine.IndexSourceAsync(new SourceFolder { Id = 1, Path = Path.GetFullPath(_dataDir) });
        var original = _db!.GetImages(1).Single();
        var oldPath = original.Path;
        var newRoot = Path.Combine(_dataDir, "new-root");
        Directory.CreateDirectory(newRoot);
        var newPath = Path.Combine(newRoot, "renamed.jpg");
        File.Move(oldPath, newPath);
        await engine.VerifyAsync();

        var plan = await engine.BuildRelocationPlanAsync(newRoot);

        var match = Assert.Single(plan.Matches);
        Assert.Equal(original.Id, match.ImageId);
        Assert.Equal(newPath, match.NewPath);
        Assert.Equal(oldPath, _db.GetImage(original.Id)!.Path);
        Assert.Equal(ImageStatus.Missing, _db.GetImage(original.Id)!.Status);
    }
}

internal sealed class DelayedOpenStorageProvider(string root, int count) : IStorageProvider
{
    private readonly StorageFileInfo[] _files = Enumerable.Range(0, count).Select(index => new StorageFileInfo
    {
        Key = Path.Combine(root, $"delayed-{index}.jpg"), Size = 100, ModifiedUtc = DateTime.UtcNow
    }).ToArray();
    public string Scheme => "delayed";
    public IEnumerable<StorageFileInfo> EnumerateFiles(string requestedRoot) => _files;
    public Task<IEnumerable<StorageFileInfo>> EnumerateFilesAsync(string requestedRoot, CancellationToken ct = default) => Task.FromResult<IEnumerable<StorageFileInfo>>(_files);
    public bool Exists(string key) => true;
    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(true);
    public Stream OpenRead(string key) => throw new NotSupportedException();
    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        return new MemoryStream(new byte[100]);
    }
    public StorageFileInfo? GetInfo(string key) => _files.FirstOrDefault(file => file.Key == key);
    public Task<StorageFileInfo?> GetInfoAsync(string key, CancellationToken ct = default) => Task.FromResult(GetInfo(key));
}


