using System.IO;
using Picer.Core.Data;
using Picer.Core.Geocoding;
using Picer.Core.Logging;
using Picer.Core.Models;
using Picer.Core.Search;

namespace Picer.Tests;

public sealed class DatabaseTests : IDisposable
{
    private readonly string _dbPath;
    private readonly PicerDatabase _db;

    public DatabaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "picer_test_" + Guid.NewGuid().ToString("n") + ".db");
        _db = new PicerDatabase(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void AddSource_ThenGetSources_ReturnsAllSources()
    {
        var s1 = _db.AddSource(@"C:\Photos\2024");
        Assert.True(s1.Id > 0);
        Assert.Equal(@"C:\Photos\2024", s1.Path);

        var s2 = _db.AddSource(@"D:\Images");
        Assert.True(s2.Id > 0);
        Assert.Equal(@"D:\Images", s2.Path);

        var s3 = _db.AddSource(@"C:\Photos\2024");
        Assert.Equal(s1.Id, s3.Id);

        var sources = _db.GetSources();
        Assert.Equal(2, sources.Count);
        Assert.Contains(sources, s => s.Path == @"C:\Photos\2024");
        Assert.Contains(sources, s => s.Path == @"D:\Images");
    }

    [Fact]
    public void UpsertImage_PersistsAndRetrievesByPath()
    {
        var source = _db.AddSource(@"C:\TestImages");
        var rec = new ImageRecord
        {
            SourceId = source.Id,
            Path = @"C:\TestImages\photo.jpg",
            FileSize = 12345,
            ModifiedUtc = DateTime.UtcNow,
            Sha256 = "abc123",
            Width = 1920,
            Height = 1080,
            Camera = "Canon EOS R5",
            Status = ImageStatus.Active,
            IndexedUtc = DateTime.UtcNow
        };
        var saved = _db.UpsertImage(rec);
        Assert.True(saved.Id > 0);

        var retrieved = _db.GetImage(saved.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(@"C:\TestImages\photo.jpg", retrieved.Path);
        Assert.Equal(1920, retrieved.Width);
        Assert.Equal(1080, retrieved.Height);
        Assert.Equal(ImageStatus.Active, retrieved.Status);
    }

    [Fact]
    public void GetImageByPath_FiltersActiveOnly_ByDefault()
    {
        var source = _db.AddSource(@"C:\Test");
        var rec = new ImageRecord
        {
            SourceId = source.Id,
            Path = @"C:\Test\a.jpg",
            FileSize = 100,
            ModifiedUtc = DateTime.UtcNow,
            Sha256 = "xyz",
            Status = ImageStatus.Missing,
            IndexedUtc = DateTime.UtcNow
        };
        _db.UpsertImage(rec);

        var active = _db.GetImageByPath(rec.Path);
        Assert.Null(active);

        var anyStatus = _db.GetImageByPath(rec.Path, anyStatus: true);
        Assert.NotNull(anyStatus);
        Assert.Equal(ImageStatus.Missing, anyStatus.Status);
    }

    [Fact]
    public void MarkMissing_UpdatesStatusCorrectly()
    {
        var source = _db.AddSource(@"C\Test");
        var r1 = new ImageRecord { SourceId = source.Id, Path = @"C\Test\a.jpg", FileSize = 100, ModifiedUtc = DateTime.UtcNow, Sha256 = "a", Status = ImageStatus.Active, IndexedUtc = DateTime.UtcNow };
        var r2 = new ImageRecord { SourceId = source.Id, Path = @"C\Test\b.jpg", FileSize = 200, ModifiedUtc = DateTime.UtcNow, Sha256 = "b", Status = ImageStatus.Active, IndexedUtc = DateTime.UtcNow };
        _db.UpsertImage(r1);
        _db.UpsertImage(r2);

        _db.MarkMissing(new[] { r1.Id });

        var check1 = _db.GetImage(r1.Id)!;
        var check2 = _db.GetImage(r2.Id)!;
        Assert.Equal(ImageStatus.Missing, check1.Status);
        Assert.Equal(ImageStatus.Active, check2.Status);
    }

    [Fact]
    public void GetMissingImages_ReturnsOnlyMissingRecords()
    {
        var source = _db.AddSource(@"C\Test");
        var a = new ImageRecord { SourceId = source.Id, Path = @"C\Test\x.jpg", FileSize = 100, ModifiedUtc = DateTime.UtcNow, Sha256 = "a", Status = ImageStatus.Active, IndexedUtc = DateTime.UtcNow };
        var b = new ImageRecord { SourceId = source.Id, Path = @"C\Test\y.jpg", FileSize = 200, ModifiedUtc = DateTime.UtcNow, Sha256 = "b", Status = ImageStatus.Active, IndexedUtc = DateTime.UtcNow };
        _db.UpsertImage(a);
        _db.UpsertImage(b);
        _db.MarkMissing(new[] { a.Id });

        var missing = _db.GetMissingImages();
        Assert.Single(missing);
        Assert.Equal(a.Id, missing[0].Id);
    }

    [Fact]
    public void RemoveSource_CascadesToImages()
    {
        var source = _db.AddSource(@"C\Test");
        for (int i = 0; i < 5; i++)
        {
            _db.UpsertImage(new ImageRecord
            {
                SourceId = source.Id,
                Path = $"C\\Test\\img{i}.jpg",
                FileSize = 100 * (i + 1),
                ModifiedUtc = DateTime.UtcNow,
                Sha256 = $"{i}",
                Status = ImageStatus.Active,
                IndexedUtc = DateTime.UtcNow
            });
        }

        var imagesBefore = _db.GetImages(source.Id, includeMissing: true);
        Assert.Equal(5, imagesBefore.Count);

        _db.RemoveSource(source.Id);

        var after = _db.GetImages(source.Id, includeMissing: true);
        Assert.Empty(after);
    }

    [Fact]
    public void GetImagesByPaths_ReturnsMatchingRecords()
    {
        var source = _db.AddSource(@"C:\Test");
        var r1 = new ImageRecord { SourceId = source.Id, Path = @"C:\Test\a.jpg", FileSize = 100, ModifiedUtc = DateTime.UtcNow, Sha256 = "a", Status = ImageStatus.Active, IndexedUtc = DateTime.UtcNow };
        var r2 = new ImageRecord { SourceId = source.Id, Path = @"C:\Test\b.jpg", FileSize = 200, ModifiedUtc = DateTime.UtcNow, Sha256 = "b", Status = ImageStatus.Active, IndexedUtc = DateTime.UtcNow };
        var r3 = new ImageRecord { SourceId = source.Id, Path = @"C:\Test\c.jpg", FileSize = 300, ModifiedUtc = DateTime.UtcNow, Sha256 = "c", Status = ImageStatus.Missing, IndexedUtc = DateTime.UtcNow };
        _db.UpsertImage(r1);
        _db.UpsertImage(r2);
        _db.UpsertImage(r3);

        var paths = new[] { @"C:\Test\a.jpg", @"C:\Test\b.jpg", @"C:\Test\nonexistent.jpg" };
        var result = _db.GetImagesByPaths(paths);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Path == @"C:\Test\a.jpg");
        Assert.Contains(result, r => r.Path == @"C:\Test\b.jpg");
        Assert.DoesNotContain(result, r => r.Path == @"C:\Test\c.jpg");
    }

    [Fact]
    public void GetImagesByPaths_EmptyInput_ReturnsEmpty()
    {
        var result = _db.GetImagesByPaths(Enumerable.Empty<string>());
        Assert.Empty(result);
    }

    [Fact]
    public void GetActiveImagesUnderDirectory_ReturnsNestedImagesAndExcludesSiblingPrefix()
    {
        var source = _db.AddSource(@"C:\Photos");
        foreach (var (path, status) in new[]
        {
            (@"C:\Photos\root.jpg", ImageStatus.Active),
            (@"C:\Photos\2026\trip.jpg", ImageStatus.Active),
            (@"C:\Photos\2026\missing.jpg", ImageStatus.Missing),
            (@"C:\Photos-old\other.jpg", ImageStatus.Active)
        })
        {
            _db.UpsertImage(new ImageRecord
            {
                SourceId = source.Id,
                Path = path,
                FileSize = 100,
                ModifiedUtc = DateTime.UtcNow,
                Sha256 = path,
                Status = status,
                IndexedUtc = DateTime.UtcNow
            });
        }

        var result = _db.GetActiveImagesUnderDirectory(@"c:\photos");

        Assert.Equal(2, result.Count);
        Assert.Contains(result, image => image.Path.EndsWith("root.jpg"));
        Assert.Contains(result, image => image.Path.EndsWith("trip.jpg"));
    }

    [Fact]
    public void Schema_IsMigratedToLatestVersionWithLocationHierarchy()
    {
        using var version = _db.Command("PRAGMA user_version");
        Assert.Equal((long)PicerDatabase.CurrentSchemaVersion, (long)version.ExecuteScalar()!);
        using var table = _db.Command("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='rejected_suggestions'");
        Assert.Equal(1L, (long)table.ExecuteScalar()!);
        using var locationTable = _db.Command("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='image_locations'");
        Assert.Equal(1L, (long)locationTable.ExecuteScalar()!);
        using var burstTables = _db.Command("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('burst_groups','burst_group_members','burst_source_state')");
        Assert.Equal(3L, (long)burstTables.ExecuteScalar()!);
    }

    [Fact]
    public void Schema_Version34MigratesFaceUserActionEventsToLatest()
    {
        var path = Path.Combine(Path.GetTempPath(), "picer_migration_34_" + Guid.NewGuid().ToString("n") + ".db");
        try
        {
            using (var legacy = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                legacy.Open();
                using var command = legacy.CreateCommand();
                command.CommandText = "PRAGMA user_version = 34";
                command.ExecuteNonQuery();
            }

            using var migrated = new PicerDatabase(path);
            using var check = migrated.Command("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='face_user_action_events'");
            Assert.Equal(1L, (long)check.ExecuteScalar()!);
            using var version = migrated.Command("PRAGMA user_version");
            Assert.Equal((long)PicerDatabase.CurrentSchemaVersion, (long)version.ExecuteScalar()!);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task LocationHierarchy_EnrichesExistingImageAndMatchesEveryAdministrativeLevel()
    {
        var source = _db.AddSource(@"D:\Photos");
        _db.UpsertImage(new ImageRecord
        {
            SourceId = source.Id,
            Path = @"D:\Photos\melbourne.jpg",
            FileSize = 100,
            ModifiedUtc = DateTime.UtcNow,
            Gps = "-37.8136,144.9631",
            Status = ImageStatus.Active,
            IndexedUtc = DateTime.UtcNow
        });
        var geocoder = new HierarchyGeocoder();
        var service = new LocationHierarchyService(_db, geocoder);

        var first = await service.EnrichPendingAsync();
        var second = await service.EnrichPendingAsync();

        Assert.Equal(1, first.SavedImages);
        Assert.Equal(0, second.CoordinateGroups);
        Assert.Equal(1, geocoder.RequestCount);
        foreach (var query in new[] { "澳大利亚", "维多利亚州", "墨尔本", "中央商务区" })
            Assert.Single(IndexSearchEngine.Search(_db, new SearchFilters { Query = query }));
    }

    [Fact]
    public void ManualLocation_MoveAndAddChild_PreservesPhotoIndex()
    {
        var source = _db.AddSource(@"D:\Photos");
        var image = _db.UpsertImage(new ImageRecord
        {
            SourceId = source.Id, Path = @"D:\Photos\manual.jpg", FileSize = 100,
            ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow,
            Status = ImageStatus.Active, Gps = "34.34,108.94"
        });
        var city = new LocationHierarchyChoice("中国 / 陕西省 / 西安市", "中国", "陕西省", "西安市");
        var district = city.AddChild("雁塔区");

        _db.SetImageLocation(image.Id, district);

        Assert.NotNull(_db.GetImage(image.Id));
        Assert.Equal(ImageStatus.Active, _db.GetImage(image.Id)!.Status);
        Assert.Contains(_db.GetLocationHierarchyChoices(), choice => choice.DisplayName == "中国 / 陕西省 / 西安市 / 雁塔区");
        Assert.Single(IndexSearchEngine.Search(_db, new SearchFilters { Query = "雁塔区" }));
    }

    [Fact]
    public void ManualLocation_BulkMove_UpdatesEverySelectedPhoto()
    {
        var source = _db.AddSource(@"D:\Photos");
        var first = _db.UpsertImage(new ImageRecord { SourceId = source.Id, Path = @"D:\Photos\one.jpg", FileSize = 1, ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow, Status = ImageStatus.Active });
        var second = _db.UpsertImage(new ImageRecord { SourceId = source.Id, Path = @"D:\Photos\two.jpg", FileSize = 1, ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow, Status = ImageStatus.Active });
        var location = new LocationHierarchyChoice("中国 / 陕西省 / 西安市", "中国", "陕西省", "西安市");

        _db.SetImageLocations([first.Id, second.Id], location);

        Assert.Equal(2, IndexSearchEngine.Search(_db, new SearchFilters { Query = "西安市" }).Count);
        Assert.Equal(2, _db.GetImageLocationHierarchyRows().Count(row => row.City == "西安市"));
    }

    private sealed class HierarchyGeocoder : IReverseGeocoder
    {
        public int RequestCount { get; private set; }

        public Task<ReverseGeocodingResult?> ReverseAsync(double latitude, double longitude, CancellationToken cancellationToken = default)
        {
            RequestCount++;
            return Task.FromResult<ReverseGeocodingResult?>(new(
                "中央商务区，墨尔本，维多利亚州，澳大利亚", "test",
                Country: "澳大利亚", Province: "维多利亚州", City: "墨尔本", District: "中央商务区"));
        }

        public Task<GeoBounds?> ResolvePlaceAsync(string query, CancellationToken cancellationToken = default) =>
            Task.FromResult<GeoBounds?>(null);
    }

    [Fact]
    public void GetImagesByPaths_BatchQuery_LargeInput()
    {
        var source = _db.AddSource(@"C:\Test");
        var insertedPaths = new List<string>();
        for (int i = 0; i < 1200; i++)
        {
            var path = $@"C:\Test\img{i:D4}.jpg";
            insertedPaths.Add(path);
            _db.UpsertImage(new ImageRecord
            {
                SourceId = source.Id,
                Path = path,
                FileSize = 100,
                ModifiedUtc = DateTime.UtcNow,
                Sha256 = $"hash{i}",
                Status = ImageStatus.Active,
                IndexedUtc = DateTime.UtcNow
            });
        }

        var queryPaths = insertedPaths.Take(600).ToList();
        queryPaths.Add(@"C:\Test\notexist.jpg");

        var result = _db.GetImagesByPaths(queryPaths);
        Assert.Equal(600, result.Count);
    }
}

[CollectionDefinition("LoggerTests", DisableParallelization = true)]
public sealed class LoggerTestsCollection
{
}

[Collection("LoggerTests")]
public sealed class LoggerTests : IDisposable
{
    private readonly string _logDir;

    public LoggerTests()
    {
        _logDir = Path.Combine(Path.GetTempPath(), "picer_log_test_" + Guid.NewGuid().ToString("n"));
        AppLogger.Initialize(_logDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_logDir, true); } catch { }
    }

    [Fact]
    public void Info_CreatesLogFile()
    {
        AppLogger.Info("Test message");
        var files = Directory.GetFiles(_logDir, "*.log");
        Assert.Single(files);
        var content = File.ReadAllText(files[0]);
        Assert.Contains("INFO: Test message", content);
    }

    [Fact]
    public void Error_IncludesException()
    {
        try { throw new InvalidOperationException("test error"); }
        catch (Exception ex) { AppLogger.Error("Something failed", ex); }

        var files = Directory.GetFiles(_logDir, "*.log");
        var content = File.ReadAllText(files[0]);
        Assert.Contains("ERROR: Something failed", content);
        Assert.Contains("test error", content);
    }

    [Fact]
    public void LogEntriesContainTimestamp()
    {
        AppLogger.Info("Timestamp test");
        var content = File.ReadAllText(Directory.GetFiles(_logDir, "*.log")[0]);
        Assert.Matches(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\]", content);
    }

    [Fact]
    public void OperationScope_WritesCorrelationAndRedactsCredential()
    {
        using (AppLogger.BeginOperation("test.operation", ("count", 3), ("token", "secret-value"))) { }

        var operationFile = Directory.GetFiles(_logDir, "picer-operations-*.jsonl").Single();
        var content = File.ReadAllText(operationFile);
        Assert.Contains("test.operation", content);
        Assert.Contains("\"durationMs\"", content);
        Assert.Contains("[REDACTED]", content);
        Assert.DoesNotContain("secret-value", content);
    }
}
