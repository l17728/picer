using System.IO;
using Picer.Core.Data;
using Picer.Core.Indexing;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Thumbnails;
using Picer.Tests.Stubs;

namespace Picer.Tests;

public sealed class SearchTests : IDisposable
{
    private readonly string _dataDir;
    private DiskBackedStorageProvider? _storage;
    private ThumbnailService? _thumbService;
    private PicerDatabase? _db;

    public SearchTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "picer_search_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dataDir);
    }

    [Theory]
    [InlineData("有多少张照片", LocalQuestionIntent.Count)]
    [InlineData("这些照片用了什么相机", LocalQuestionIntent.CameraSummary)]
    [InlineData("照片里的人是谁", LocalQuestionIntent.Unsupported)]
    public void LocalQuestionInterpreter_IsExplicitAboutSupportedIntents(string question, LocalQuestionIntent expected)
    {
        var result = LocalQuestionInterpreter.Interpret(question);
        Assert.Equal(expected, result.Intent);
        Assert.Equal(expected != LocalQuestionIntent.Unsupported, result.Supported);
        Assert.False(string.IsNullOrWhiteSpace(result.Explanation));
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
    public async Task Search_FullTextQuery_ReturnsMatchingImages()
    {
        // Set up files with distinctive names
        AddFile("photos/photo_a.jpg", 0x41, 100);
        AddFile("photos/banana_photo.png", 0x42, 200);
        AddFile("photos/cherry.webp", 0x43, 300);

        var root = Path.GetFullPath(_dataDir);
        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));
        var engine = new IndexEngine(_db, _storage = new DiskBackedStorageProvider(),
            _thumbService = new ThumbnailService(Path.Combine(_dataDir, "thumbs")));

        await engine.IndexSourceAsync(new SourceFolder { Id = 1, Path = root });

        var filters = new SearchFilters { Query = "banana" };
        var matches = IndexSearchEngine.Search(_db, filters);

        Assert.Single(matches);
        Assert.Contains("banana", matches[0].Path);
    }

    [Fact]
    public async Task Search_NoQueryReturnsAllActive()
    {
        AddFile("photos/a.jpg", 0x41, 100);
        AddFile("photos/b.png", 0x42, 200);

        var root = Path.GetFullPath(_dataDir);
        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));
        var engine = new IndexEngine(_db, _storage = new DiskBackedStorageProvider(),
            _thumbService = new ThumbnailService(Path.Combine(_dataDir, "thumbs")));

        await engine.IndexSourceAsync(new SourceFolder { Id = 1, Path = root });

        var filters = new SearchFilters();
        var matches = IndexSearchEngine.Search(_db, filters);

        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public async Task Search_TimeOfDayFiltersUsingLocalCaptureHour()
    {
        AddFile("photos/day.jpg", 0x41, 100);
        AddFile("photos/night.jpg", 0x42, 100);
        var root = Path.GetFullPath(_dataDir);
        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));
        var engine = new IndexEngine(_db, _storage = new DiskBackedStorageProvider(),
            _thumbService = new ThumbnailService(Path.Combine(_dataDir, "thumbs")));
        await engine.IndexSourceAsync(new SourceFolder { Id = 1, Path = root });
        var day = _db.GetImages(1).Single(image => image.Path.EndsWith("day.jpg"));
        var night = _db.GetImages(1).Single(image => image.Path.EndsWith("night.jpg"));
        using (var command = _db.Command("UPDATE images SET taken_utc=$time WHERE id=$id"))
        {
            command.Parameters.AddWithValue("$time", DateTime.Today.AddHours(10).ToUniversalTime().ToString("o")); command.Parameters.AddWithValue("$id", day.Id); command.ExecuteNonQuery();
            command.Parameters.Clear(); command.Parameters.AddWithValue("$time", DateTime.Today.AddHours(22).ToUniversalTime().ToString("o")); command.Parameters.AddWithValue("$id", night.Id); command.ExecuteNonQuery();
        }
        Assert.Equal("day.jpg", Path.GetFileName(Assert.Single(IndexSearchEngine.Search(_db, new SearchFilters { TimeOfDay = TimeOfDayFilter.Day })).Path));
        Assert.Equal("night.jpg", Path.GetFileName(Assert.Single(IndexSearchEngine.Search(_db, new SearchFilters { TimeOfDay = TimeOfDayFilter.Night })).Path));
    }

    [Fact]
    public async Task MediaSearchTool_ValidatesAndExecutesStructuredTimeQuery()
    {
        AddFile("photos/day.jpg", 0x41, 100);
        var root = Path.GetFullPath(_dataDir);
        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));
        var engine = new IndexEngine(_db, _storage = new DiskBackedStorageProvider(),
            _thumbService = new ThumbnailService(Path.Combine(_dataDir, "thumbs")));
        await engine.IndexSourceAsync(new SourceFolder { Id = 1, Path = root });
        var image = _db.GetImages(1).Single();
        using (var command = _db.Command("UPDATE images SET taken_utc=$time WHERE id=$id"))
        { command.Parameters.AddWithValue("$time", DateTime.Today.AddHours(10).ToUniversalTime().ToString("o")); command.Parameters.AddWithValue("$id", image.Id); command.ExecuteNonQuery(); }
        var result = new MediaSearchTool(_db).Execute(new MediaSearchRequest(TimeOfDay: TimeOfDayFilter.Day));
        Assert.Single(result.Items);
        Assert.Equal(MediaTypeFilter.Photo, result.Items[0].MediaType);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaSearchTool(_db).Execute(new MediaSearchRequest(Limit: 0)));
    }

    [Fact]
    public async Task Search_UsesIncrementalMediaTextIndex()
    {
        AddFile("photos/unknown.jpg", 0x41, 100);
        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));
        var engine = new IndexEngine(_db, _storage = new DiskBackedStorageProvider(),
            _thumbService = new ThumbnailService(Path.Combine(_dataDir, "thumbs")));
        await engine.IndexSourceAsync(new SourceFolder { Id = 1, Path = Path.GetFullPath(_dataDir) });
        var image = _db.GetImages(1).Single();
        new MediaTextIndexRepository(_db).UpsertImage(image, ["上海 外滩 红色裙子", "登山"], "test:v1");

        var result = IndexSearchEngine.Search(_db, new SearchFilters { Query = "外滩" });
        Assert.Single(result);
        Assert.Equal(image.Id, result[0].Id);
    }

    [Fact]
    public async Task Search_ExcludesMissingWhenFlagSet()
    {
        AddFile("photos/a.jpg", 0x41, 100);
        AddFile("photos/b.png", 0x42, 200);

        var root = Path.GetFullPath(_dataDir);
        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));
        var engine = new IndexEngine(_db, _storage = new DiskBackedStorageProvider(),
            _thumbService = new ThumbnailService(Path.Combine(_dataDir, "thumbs")));

        await engine.IndexSourceAsync(new SourceFolder { Id = 1, Path = root });

        // Mark b.png as missing
        var recB = _db.GetImages(1).Single(r => r.Path.EndsWith("b.png"));
        _db.MarkMissing(new[] { recB.Id });
        File.Delete(recB.Path);

        // With includeMissing=false
        var filters = new SearchFilters { IncludeMissing = false };
        var active = IndexSearchEngine.Search(_db, filters);
        Assert.Single(active);
        Assert.DoesNotContain(active, r => r.Path.EndsWith("b.png"));

        // With includeMissing=true
        filters.IncludeMissing = true;
        var all = IndexSearchEngine.Search(_db, filters);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task CountMatches_SameAsSearchCount()
    {
        AddFile("photos/a.jpg", 0x41, 100);
        AddFile("photos/b.png", 0x42, 200);
        AddFile("photos/c.gif", 0x43, 300);

        var root = Path.GetFullPath(_dataDir);
        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));
        var engine = new IndexEngine(_db, _storage = new DiskBackedStorageProvider(),
            _thumbService = new ThumbnailService(Path.Combine(_dataDir, "thumbs")));

        await engine.IndexSourceAsync(new SourceFolder { Id = 1, Path = root });

        var filters = new SearchFilters();
        var count = IndexSearchEngine.CountMatches(_db, filters);
        var results = IndexSearchEngine.Search(_db, filters);

        Assert.Equal(results.Count, count);
    }

    [Fact]
    public async Task Search_FilterBySourceId_LimitsResults()
    {
        var dir1 = Path.Combine(_dataDir, "src1");
        var dir2 = Path.Combine(_dataDir, "src2");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);

        File.WriteAllText(Path.Combine(dir1, "photo.jpg"), "a");
        File.WriteAllText(Path.Combine(dir2, "other.png"), "b");

        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));
        var storage = new DiskBackedStorageProvider();

        var engine1 = new IndexEngine(_db, storage, _thumbService = new ThumbnailService(Path.Combine(_dataDir, "thumbs")));
        await engine1.IndexSourceAsync(new SourceFolder { Id = 1, Path = dir1 });

        var engine2 = new IndexEngine(_db, storage, _thumbService!);
        await engine2.IndexSourceAsync(new SourceFolder { Id = 2, Path = dir2 });

        var filtersSrc1 = new SearchFilters { SourceId = 1, IncludeMissing = false };
        var src1Results = IndexSearchEngine.Search(_db, filtersSrc1);
        Assert.Single(src1Results);
        Assert.Contains("photo.jpg", src1Results[0].Path);

        var filtersSrc2 = new SearchFilters { SourceId = 2, IncludeMissing = false };
        var src2Results = IndexSearchEngine.Search(_db, filtersSrc2);
        Assert.Single(src2Results);
        Assert.Contains("other.png", src2Results[0].Path);
    }

    [Fact]
    public async Task Search_AfterBatchIndexing_ReturnsAllFiles()
    {
        for (int i = 0; i < 30; i++)
            AddFile($"photos/img_{i:D2}.jpg", (byte)i, 100 + i);

        var root = Path.GetFullPath(_dataDir);
        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));
        var engine = new IndexEngine(_db, new DiskBackedStorageProvider(),
            new ThumbnailService(Path.Combine(_dataDir, "thumbs")));

        await engine.IndexSourceAsync(new SourceFolder { Id = 1, Path = root });

        var results = IndexSearchEngine.Search(_db, new SearchFilters());
        Assert.Equal(30, results.Count);
    }

    [Fact]
    public async Task Search_FullTextMatchesPartialFilename()
    {
        AddFile("photos/vacation_beach.jpg", 0x41, 100);
        AddFile("photos/mountain_hike.png", 0x42, 200);
        AddFile("photos/beach_sunset.webp", 0x43, 300);

        var root = Path.GetFullPath(_dataDir);
        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));
        var engine = new IndexEngine(_db, new DiskBackedStorageProvider(),
            new ThumbnailService(Path.Combine(_dataDir, "thumbs")));

        await engine.IndexSourceAsync(new SourceFolder { Id = 1, Path = root });

        var filters = new SearchFilters { Query = "beach" };
        var matches = IndexSearchEngine.Search(_db, filters);

        Assert.Equal(2, matches.Count);
        Assert.All(matches, m => Assert.Contains("beach", m.Path));
    }

    [Fact]
    public async Task Search_SortByFileName_ReturnsAlphabeticalOrder()
    {
        AddFile("photos/zeta.jpg", 0x41, 100);
        AddFile("photos/alpha.png", 0x42, 200);
        AddFile("photos/middle.gif", 0x43, 300);

        var root = Path.GetFullPath(_dataDir);
        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));
        var engine = new IndexEngine(_db, new DiskBackedStorageProvider(),
            new ThumbnailService(Path.Combine(_dataDir, "thumbs")));

        await engine.IndexSourceAsync(new SourceFolder { Id = 1, Path = root });

        var filters = new SearchFilters { SortBy = SortField.FileName, SortDir = SortDirection.Ascending };
        var matches = IndexSearchEngine.Search(_db, filters);

        Assert.Equal(3, matches.Count);
        Assert.Contains("alpha", matches[0].Path);
        Assert.Contains("middle", matches[1].Path);
        Assert.Contains("zeta", matches[2].Path);
    }

    [Fact]
    public async Task SearchPaged_UsesStableTieBreakerWithoutDuplicatesOrGaps()
    {
        for (var i = 0; i < 7; i++) AddFile($"photos/same_size_{i}.jpg", (byte)i, 100);
        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));
        var engine = new IndexEngine(_db, new DiskBackedStorageProvider(),
            new ThumbnailService(Path.Combine(_dataDir, "thumbs")));
        await engine.IndexSourceAsync(new SourceFolder { Id = 1, Path = Path.GetFullPath(_dataDir) });
        var filters = new SearchFilters { SortBy = SortField.FileSize, SortDir = SortDirection.Ascending };

        var first = IndexSearchEngine.SearchPaged(_db, filters, 3);
        var second = IndexSearchEngine.SearchPaged(_db, filters, 3, first.NextCursor);
        var third = IndexSearchEngine.SearchPaged(_db, filters, 3, second.NextCursor);
        var ids = first.Items.Concat(second.Items).Concat(third.Items).Select(item => item.Id).ToList();

        Assert.Equal(7, ids.Count);
        Assert.Equal(7, ids.Distinct().Count());
        Assert.Equal(7, first.TotalCount);
        Assert.Null(third.NextCursor);
    }

    [Theory]
    [InlineData("34.3416000,108.9398000")]
    [InlineData("34° 20' 29.76\" N, 108° 56' 23.28\" E")]
    public void GeoPlaceSearch_MatchesXianCoordinatesInDecimalAndDmsFormats(string gps)
    {
        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));
        var source = _db.AddSource(Path.Combine(_dataDir, "photos"));
        _db.UpsertImage(new ImageRecord
        {
            SourceId = source.Id,
            Path = Path.Combine(source.Path, "xian.jpg"),
            FileSize = 100,
            ModifiedUtc = DateTime.UtcNow,
            IndexedUtc = DateTime.UtcNow,
            Status = ImageStatus.Active,
            Gps = gps
        });
        _db.UpsertImage(new ImageRecord
        {
            SourceId = source.Id,
            Path = Path.Combine(source.Path, "beijing.jpg"),
            FileSize = 100,
            ModifiedUtc = DateTime.UtcNow,
            IndexedUtc = DateTime.UtcNow,
            Status = ImageStatus.Active,
            Gps = "39.9042,116.4074"
        });

        var bounds = GeoPlaceResolver.Resolve("西安");
        var page = IndexSearchEngine.SearchPaged(_db, new SearchFilters { Query = "", GeoBounds = bounds });

        Assert.NotNull(bounds);
        Assert.Single(page.Items);
        Assert.EndsWith("xian.jpg", page.Items[0].Path);
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public void XianAndShangluoFallbackAreas_DoNotReturnTheSameBorderPhotos()
    {
        _db = new PicerDatabase(Path.Combine(_dataDir, "test.db"));
        var source = _db.AddSource(Path.Combine(_dataDir, "photos"));
        foreach (var (name, gps) in new[]
                 {
                     ("lantian.jpg", "34.0741,109.2710"),
                     ("shangzhou.jpg", "33.8700,109.9400")
                 })
            _db.UpsertImage(new ImageRecord
            {
                SourceId = source.Id, Path = Path.Combine(source.Path, name), FileSize = 100,
                ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow,
                Status = ImageStatus.Active, Gps = gps
            });

        var xian = IndexSearchEngine.Search(_db, new SearchFilters { GeoBounds = GeoPlaceResolver.Resolve("西安") });
        var shangluo = IndexSearchEngine.Search(_db, new SearchFilters { GeoBounds = GeoPlaceResolver.Resolve("商洛") });

        Assert.Single(xian);
        Assert.EndsWith("lantian.jpg", xian[0].Path);
        Assert.Single(shangluo);
        Assert.EndsWith("shangzhou.jpg", shangluo[0].Path);
        Assert.Empty(xian.Select(image => image.Id).Intersect(shangluo.Select(image => image.Id)));
    }
}
