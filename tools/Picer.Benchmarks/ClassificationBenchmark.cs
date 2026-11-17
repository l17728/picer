using System.Diagnostics;
using System.Text.Json;
using Picer.Core.Classification;
using Picer.Core.Data;
using Picer.Core.Models;

internal static class ClassificationBenchmark
{
    internal static void Run(string destination, int samples)
    {
        if (samples is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(samples));
        var root = Path.Combine(Path.GetFullPath(destination), "classification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var db = new PicerDatabase(Path.Combine(root, "picer.db"));
        var source = db.AddSource(root);
        var now = DateTime.UtcNow;
        db.UpsertImagesBatch(Enumerable.Range(0, 10000).Select(i => new ImageRecord { SourceId = source.Id,
            Path = Path.Combine(root, i + ".jpg"), FileSize = 100, ModifiedUtc = now, Width = 600, Height = 400, IndexedUtc = now }));
        var config = new ClassificationConfiguration(true, "fixture-no-model", "local", false);
        var repository = new ClassificationRepository(db); repository.Configure(config);
        var taxonomy = repository.Taxonomy();
        var extra = Enumerable.Range(0, Math.Max(0, 200 - taxonomy.Labels.Count))
            .Select(i => taxonomy.Labels.First(label => label.Assignable) with { Id = "benchmark.extra." + i, Name = "基准标签" + i, Aliases = [] }).ToArray();
        taxonomy = taxonomy with { Labels = taxonomy.Labels.Concat(extra).ToArray(),
            Definitions = taxonomy.Definitions.Concat(extra.Select(label => new ClassificationDefinitionVersion(label.Id, 1))).ToArray() };
        using (var save = db.Command("INSERT INTO classification_v2_taxonomy VALUES(1,$revision,$discovery,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload"))
        {
            save.Parameters.AddWithValue("$revision", taxonomy.Revision); save.Parameters.AddWithValue("$discovery", taxonomy.DiscoveryRevision);
            save.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(taxonomy)); save.ExecuteNonQuery();
        }
        var contexts = repository.Contexts(config);
        using (var transaction = db.BeginTransaction())
        using (var insert = db.Command("INSERT INTO classification_v2_work(image_id,success_input,success_fingerprint,outcome) VALUES($id,$input,$fingerprint,'succeeded')"))
        using (var label = db.Command("INSERT INTO classification_v2_labels VALUES($id,$label)"))
        {
            insert.Transaction = transaction;
            label.Transaction = transaction;
            var labelImage = label.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
            var labelId = label.Parameters.Add("$label", Microsoft.Data.Sqlite.SqliteType.Text);
            var assignable = taxonomy.Labels.Where(item => item.Active && item.Assignable).Select(item => item.Id).ToArray();
            var id = insert.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
            var input = insert.Parameters.Add("$input", Microsoft.Data.Sqlite.SqliteType.Text);
            var fingerprint = insert.Parameters.Add("$fingerprint", Microsoft.Data.Sqlite.SqliteType.Text);
            foreach (var context in contexts)
            {
                id.Value = context.Image.Id; input.Value = JsonSerializer.Serialize(context.Input); fingerprint.Value = ClassificationInputFingerprint.Compute(context.Input); insert.ExecuteNonQuery();
                labelImage.Value = context.Image.Id;
                for (var j = 0; j < 3; j++) { labelId.Value = assignable[(int)((context.Image.Id * 3 + j) % assignable.Length)]; label.ExecuteNonQuery(); }
            }
            transaction.Commit();
        }
        var scans = new List<double>(); var reads = new List<double>();
        for (var i = 0; i < samples; i++)
        {
            var timer = Stopwatch.StartNew();
            var plan = repository.Plan(Guid.NewGuid().ToString("N"), config);
            scans.Add(timer.Elapsed.TotalMilliseconds);
            if (plan.Items.Count != 0 || plan.Unchanged != 10000) throw new InvalidDataException("classification_benchmark_scan_changed");
            timer.Restart();
            var page = repository.Browse(new(new([], [])));
            reads.Add(timer.Elapsed.TotalMilliseconds);
            if (page.Coverage.Total != 10000 || page.Photos.Count != 60) throw new InvalidDataException("classification_benchmark_count_invalid");
        }
        static object Summary(List<double> values) { var sorted = values.Order().ToArray(); return new { p50 = sorted[sorted.Length / 2], p95 = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1], samples = values }; }
        using var cache = new ClassificationBrowseCache(db);
        _ = cache.Browse(new(new([], [])));
        var warmReads = new List<double>(); var filterReads = new List<double>();
        var filteredRequest = new ClassificationBrowseRequest(new([new(ClassificationMatchMode.Any, ["subject.people", "scene.forest"])], []));
        for (var i = 0; i < samples; i++)
        {
            var timer = Stopwatch.StartNew();
            var page = cache.Browse(new(new([], [])));
            warmReads.Add(timer.Elapsed.TotalMilliseconds);
            if (page.Coverage.Total != 10000 || page.Photos.Count != 60) throw new InvalidDataException("classification_cache_count_invalid");
            timer.Restart();
            var filtered = cache.Browse(filteredRequest);
            filterReads.Add(timer.Elapsed.TotalMilliseconds);
            if (filtered.Matched <= 0 || filtered.Matched >= 10000 || filtered.Coverage.Suggested != 10000)
                throw new InvalidDataException("classification_filter_count_invalid");
        }
        Console.WriteLine(JsonSerializer.Serialize(new { root, photos = 10000, labels = repository.Taxonomy().Labels.Count,
            labelsPerPhoto = 3, modelCalls = 0, unchangedScanMs = Summary(scans), browseMs = Summary(reads), cachedBrowseMs = Summary(warmReads), cachedFilterMs = Summary(filterReads) }));
    }
}
