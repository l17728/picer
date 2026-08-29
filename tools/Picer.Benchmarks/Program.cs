using System.Diagnostics;
using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Search;

var count = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 1_000;
if (count is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(count));
var iterations = args.Length > 1 && int.TryParse(args[1], out var parsedIterations) ? parsedIterations : 30;
var benchmarkBase = args.Length > 2 ? Path.GetFullPath(args[2]) : @"D:\PicerData\benchmarks";
Directory.CreateDirectory(benchmarkBase);
var root = Path.Combine(benchmarkBase, "picer-benchmark-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var dbPath = Path.Combine(root, "benchmark.db");

try
{
    using var db = new PicerDatabase(dbPath);
    var source = db.AddSource(Path.Combine(root, "photos"));
    var generation = Stopwatch.StartNew();
    db.Command("DROP TRIGGER IF EXISTS images_ai").ExecuteNonQuery();
    using (var tx = db.BeginTransaction())
    {
        using var command = db.Command("""
            INSERT INTO images(source_id,path,file_size,modified_utc,sha256,width,height,taken_utc,camera,gps,status,indexed_utc,ahash,dhash)
            VALUES($source,$path,$size,$modified,$sha,$width,$height,$taken,$camera,NULL,'active',$indexed,$ahash,$dhash)
            """);
        command.Transaction = tx;
        command.Parameters.Add("$source", Microsoft.Data.Sqlite.SqliteType.Integer);
        command.Parameters.Add("$path", Microsoft.Data.Sqlite.SqliteType.Text);
        command.Parameters.Add("$size", Microsoft.Data.Sqlite.SqliteType.Integer);
        command.Parameters.Add("$modified", Microsoft.Data.Sqlite.SqliteType.Text);
        command.Parameters.Add("$sha", Microsoft.Data.Sqlite.SqliteType.Text);
        command.Parameters.Add("$width", Microsoft.Data.Sqlite.SqliteType.Integer);
        command.Parameters.Add("$height", Microsoft.Data.Sqlite.SqliteType.Integer);
        command.Parameters.Add("$taken", Microsoft.Data.Sqlite.SqliteType.Text);
        command.Parameters.Add("$camera", Microsoft.Data.Sqlite.SqliteType.Text);
        command.Parameters.Add("$indexed", Microsoft.Data.Sqlite.SqliteType.Text);
        command.Parameters.Add("$ahash", Microsoft.Data.Sqlite.SqliteType.Integer);
        command.Parameters.Add("$dhash", Microsoft.Data.Sqlite.SqliteType.Integer);
        var now = DateTime.UtcNow;
        for (var i = 0; i < count; i++)
        {
            command.Parameters["$source"].Value = source.Id;
            command.Parameters["$path"].Value = Path.Combine(source.Path, $"trip_{i % 97:D2}", $"image_{i:D7}.jpg");
            command.Parameters["$size"].Value = 100_000 + i % 8_000_000;
            command.Parameters["$modified"].Value = now.AddMinutes(-i).ToString("O");
            command.Parameters["$sha"].Value = i.ToString("x64");
            command.Parameters["$width"].Value = 1024 + i % 5000;
            command.Parameters["$height"].Value = 768 + i % 3000;
            command.Parameters["$taken"].Value = now.AddDays(-(i % 3650)).ToString("O");
            command.Parameters["$camera"].Value = i % 3 == 0 ? "Canon R5" : i % 3 == 1 ? "Sony A7" : "Nikon Z8";
            command.Parameters["$indexed"].Value = now.ToString("O");
            command.Parameters["$ahash"].Value = i;
            command.Parameters["$dhash"].Value = i * 3L;
            command.ExecuteNonQuery();
        }
        tx.Commit();
    }
    db.Command("""
        INSERT INTO images_fts(rowid, filename, camera, gps, width, height, file_size)
        SELECT id, path, camera, gps, width, height, file_size FROM images;
        CREATE TRIGGER images_ai AFTER INSERT ON images BEGIN
            INSERT INTO images_fts(rowid, filename, camera, gps, width, height, file_size)
            VALUES (new.id, new.path, new.camera, new.gps, new.width, new.height, new.file_size);
        END;
        """).ExecuteNonQuery();
    generation.Stop();

    for (var warm = 0; warm < 3; warm++)
        _ = await IndexSearchEngine.SearchPagedAsync(db, new SearchFilters { Query = "trip_42", SortBy = SortField.TakenDate, SortDir = SortDirection.Descending });
    var durations = new List<double>(iterations);
    var allocatedBefore = GC.GetTotalAllocatedBytes(true);
    for (var i = 0; i < iterations; i++)
    {
        var watch = Stopwatch.StartNew();
        _ = await IndexSearchEngine.SearchPagedAsync(db, new SearchFilters
        {
            Query = $"trip_{i % 97:D2}",
            CameraBrand = i % 4 == 0 ? "Canon" : null,
            SortBy = SortField.TakenDate,
            SortDir = SortDirection.Descending
        });
        watch.Stop();
        durations.Add(watch.Elapsed.TotalMilliseconds);
    }
    durations.Sort();
    var broadDurations = new List<double>(3);
    for (var i = 0; i < 3; i++)
    {
        var watch = Stopwatch.StartNew();
        _ = await IndexSearchEngine.SearchPagedAsync(db, new SearchFilters { Query = "image", SortBy = SortField.TakenDate, SortDir = SortDirection.Descending });
        broadDurations.Add(watch.Elapsed.TotalMilliseconds);
    }
    broadDurations.Sort();
    var process = Process.GetCurrentProcess();
    var report = new
    {
        generatedUtc = DateTime.UtcNow,
        records = count,
        iterations,
        generationSeconds = generation.Elapsed.TotalSeconds,
        generationThroughputPerSecond = count / generation.Elapsed.TotalSeconds,
        selectiveSearchMs = new { p50 = Percentile(durations, .50), p95 = Percentile(durations, .95), max = durations[^1] },
        broadFullLibraryStressMs = new { p50 = Percentile(broadDurations, .50), max = broadDurations[^1] },
        searchBudgetMs = count >= 1_000_000 ? 300 : 150,
        meetsSelectiveSearchBudget = Percentile(durations, .95) < (count >= 1_000_000 ? 300 : 150),
        workingSetMb = process.WorkingSet64 / 1024d / 1024d,
        allocatedMb = (GC.GetTotalAllocatedBytes(false) - allocatedBefore) / 1024d / 1024d,
        gc = new { gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2) }
    };
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

static double Percentile(IReadOnlyList<double> values, double percentile) =>
    values[Math.Clamp((int)Math.Ceiling(values.Count * percentile) - 1, 0, values.Count - 1)];
