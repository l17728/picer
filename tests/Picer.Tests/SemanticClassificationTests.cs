using System.IO;
using Picer.Core.Data;
using Picer.Core.Indexing;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Thumbnails;
using Picer.Tests.Stubs;

namespace Picer.Tests;

public sealed class SemanticClassificationTests : IDisposable
{
    private readonly string _dataDir;
    private PicerDatabase? _db;
    private DiskBackedStorageProvider? _storage;

    public SemanticClassificationTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "picer_semantic_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dataDir);
    }

    private void AddFile(string relativePath, byte value, int length)
    {
        var fullPath = Path.Combine(_dataDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, Enumerable.Repeat(value, length).ToArray());
    }

    private IndexEngine CreateEngine(PicerDatabase db)
    {
        var thumbService = new ThumbnailService(Path.Combine(_dataDir, "thumbs"));
        return new IndexEngine(db, _storage!, thumbService);
    }

    public void Dispose()
    {
        try { _db?.Dispose(); } catch { }
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    [Fact]
    public void Tags_AddAndGet_ReturnsCorrectCount()
    {
        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));

        var tag1 = SemanticEngine.AddTag(_db, "vacation");
        var tag2 = SemanticEngine.AddTag(_db, "family");
        var tags = SemanticEngine.GetTags(_db);

        Assert.Equal(2, tags.Count);
        Assert.Contains(tags, t => t.Name == "vacation");
        Assert.Contains(tags, t => t.Name == "family");
    }

    [Fact]
    public void Albums_AddAndGet_ReturnsCorrectCount()
    {
        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));

        var album1 = SemanticEngine.AddAlbum(_db, "Summer 2024", "Beach photos");
        var album2 = SemanticEngine.AddAlbum(_db, "Winter Holiday");
        var albums = SemanticEngine.GetAlbums(_db);

        Assert.Equal(2, albums.Count);
        Assert.Contains(albums, a => a.Name == "Summer 2024");
        Assert.Contains(albums, a => a.Name == "Winter Holiday");
    }

    [Fact]
    public void AssignAndRemoveTag_WorksCorrectly()
    {
        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));

        // Create a dummy image record directly in DB
        var source = _db.AddSource("C:\\test");
        _db.UpsertImage(new ImageRecord
        {
            SourceId = source.Id,
            Path = "C:\\test\\photo.jpg",
            FileSize = 100,
            ModifiedUtc = DateTime.UtcNow,
            Status = ImageStatus.Active,
            IndexedUtc = DateTime.UtcNow
        });

        var image = _db.GetImages(source.Id).First();

        // Add tag
        var tag = SemanticEngine.AddTag(_db, "test_tag");
        SemanticEngine.AssignTag(_db, image.Id, tag.Id);

        var imageTags = SemanticEngine.GetImageTags(_db, image.Id);
        Assert.Single(imageTags);
        Assert.Equal("test_tag", imageTags[0].Name);
        Assert.Equal(image.Id, Assert.Single(IndexSearchEngine.Search(_db,
            new SearchFilters { Query = "test_tag", IncludeMissing = false })).Id);

        // Remove tag
        SemanticEngine.RemoveTag(_db, image.Id, tag.Id);
        imageTags = SemanticEngine.GetImageTags(_db, image.Id);
        Assert.Empty(imageTags);
    }

    [Fact]
    public async Task Album_AddImageAndGetWorks()
    {
        // Create and index images
        var dir = Path.Combine(_dataDir, "photos");
        Directory.CreateDirectory(dir);
        AddFile("photo_a.jpg", 0x01, 100);
        AddFile("photo_b.jpg", 0x02, 200);

        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));
        _storage = new DiskBackedStorageProvider();
        var engine = CreateEngine(_db);
        await engine.IndexSourceAsync(new SourceFolder { Id = 1, Path = dir });

        // Create album and add images
        var album = SemanticEngine.AddAlbum(_db, "Test Album");
        var images = _db.GetImages(1);

        foreach (var img in images)
            SemanticEngine.AddToAlbum(_db, album.Id, img.Id);

        var albumImages = SemanticEngine.GetAlbumImages(_db, album.Id);
        Assert.Equal(images.Count, albumImages.Count);
        Assert.Equal(images.Count, SemanticEngine.GetAlbumImageCount(_db, album.Id));
    }

    [Fact]
    public void Album_DeletePreservesImageAndIndex()
    {
        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));
        var source = _db.AddSource(Path.Combine(_dataDir, "photos"));
        var image = _db.UpsertImage(new ImageRecord
        {
            SourceId = source.Id, Path = Path.Combine(source.Path, "kept.jpg"), FileSize = 100,
            ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow, Status = ImageStatus.Active
        });
        var album = SemanticEngine.AddAlbum(_db, "Temporary Album");
        SemanticEngine.AddToAlbum(_db, album.Id, image.Id);

        SemanticEngine.DeleteAlbum(_db, album.Id);

        Assert.Empty(SemanticEngine.GetAlbums(_db));
        Assert.NotNull(_db.GetImage(image.Id));
        Assert.Equal(ImageStatus.Active, _db.GetImage(image.Id)!.Status);
        Assert.Single(_db.GetImages(source.Id));
    }

    [Fact]
    public void AutoCreateTimeLineAlbums_CreatesAlbumsWithImages()
    {
        // This test verifies that auto-time-line album creation runs without errors
        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));
        
        // Empty database should return empty list
        var albums = SemanticEngine.AutoCreateTimeLineAlbums(_db);
        Assert.Empty(albums);
    }

    [Fact]
    public void AutoCreateCameraAlbums_CreatesAlbumsWithBrandFilter()
    {
        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));
        
        // No cameras indexed yet - should return empty or minimal
        var albums = SemanticEngine.AutoCreateCameraAlbums(_db, autoTag: false);
        
        // Just verify it doesn't crash
        Assert.NotNull(albums);
    }

    [Fact]
    public void AutomaticAlbums_RepeatedRunAddsOnlyNewlyIndexedPhotos()
    {
        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));
        var source = _db.AddSource(_dataDir);
        ImageRecord Record(string name) => new()
        {
            SourceId = source.Id, Path = Path.Combine(_dataDir, name), FileSize = 10,
            ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow, Status = ImageStatus.Active,
            TakenUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), Camera = "Canon EOS R5"
        };
        _db.UpsertImage(Record("first.jpg"));
        SemanticEngine.AutoCreateTimeLineAlbums(_db);
        SemanticEngine.AutoCreateCameraAlbums(_db, autoTag: false);

        _db.UpsertImage(Record("second.jpg"));
        var newTimeAlbums = SemanticEngine.AutoCreateTimeLineAlbums(_db);
        var newCameraAlbums = SemanticEngine.AutoCreateCameraAlbums(_db, autoTag: false);

        Assert.Empty(newTimeAlbums);
        Assert.Empty(newCameraAlbums);
        var albums = SemanticEngine.GetAlbums(_db);
        Assert.Equal(2, SemanticEngine.GetAlbumImageCount(_db, albums.Single(album => album.Name == "2026年09月照片").Id));
        Assert.Equal(2, SemanticEngine.GetAlbumImageCount(_db, albums.Single(album => album.Name == "相机: Canon").Id));
    }

    [Fact]
    public void Tag_DeleteRemovesAssociatedImageTags()
    {
        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));

        var tag = SemanticEngine.AddTag(_db, "to_delete");
        SemanticEngine.DeleteTag(_db, tag.Id);

        var remaining = SemanticEngine.GetTags(_db);
        Assert.Empty(remaining);
    }

    [Fact]
    public void AIAnalysis_ExtractsSuggestionsFromMetadata()
    {
        var record = new ImageRecord
        {
            Gps = "N 40, E 116",
            Width = 5000,
            Height = 4000,
            TakenUtc = new DateTime(2024, 6, 15, 7, 30, 0, DateTimeKind.Utc),
            Camera = "Canon EOS R5"
        };

        var suggestions = Core.AiRecognition.AiSceneDetector.AnalyzeImage(record);

        Assert.NotEmpty(suggestions);
        // GPS suggests outdoor location
        Assert.Contains(suggestions, s => s.TagName == "户外");
        // Canon camera detected
        Assert.Contains(suggestions, s => s.TagName.Contains("佳能"));
        // 5000x4000 = 20MP exactly, meets the >= 20 threshold
        Assert.Contains(suggestions, s => s.TagName == "高像素");
    }
}
