using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Video;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class AnalysisBillingMigrationTests
{
    [Fact]
    public void Version42SeedsPhotoAndVideoCostsOnceWithoutMediaReferences()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-billing-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "picer.db");
        try
        {
            using (var current = new PicerDatabase(path))
            {
                var source = current.AddSource(root);
                var image = current.UpsertImage(new ImageRecord { SourceId = source.Id, Path = Path.Combine(root, "photo.jpg"),
                    FileSize = 1, ModifiedUtc = DateTime.UtcNow, Status = ImageStatus.Active });
                var analyzer = new Analyzer();
                var photos = new CloudVisionRepository(current);
                photos.Save(image, analyzer, "none", "test", new CloudVisionResult("photo", [], [], [], [])
                    { Usage = new CloudVisionUsage(10, 20, .25m) }, "photo-request");
                var videoPath = Path.Combine(root, "video.mp4"); File.WriteAllBytes(videoPath, [1, 2, 3]);
                var videos = new VideoRepository(current); var video = videos.Upsert(source.Id, videoPath);
                videos.SaveSummary(video.Id, analyzer, video, new CloudVisionResult("video", [], [], [], [])
                    { Usage = new CloudVisionUsage(30, 40, .75m) }, "video-request");
            }
            using (var legacy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                legacy.Open();
                LegacySchemaFixture.RevertVersion43(legacy);
                using var version = legacy.CreateCommand(); version.CommandText = "PRAGMA user_version";
                Assert.Equal(42L, version.ExecuteScalar());
                using var absent = legacy.CreateCommand();
                absent.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('analysis_billing_events','analysis_budget_reservations','idx_images_directory_path','idx_videos_directory_path')";
                Assert.Equal(0L, absent.ExecuteScalar());
            }
            string[] initialRequestIds = [];
            for (var restart = 0; restart < 2; restart++)
            {
                using var upgraded = new PicerDatabase(path);
                using var totals = upgraded.Command("SELECT COUNT(*),SUM(input_tokens),SUM(output_tokens),SUM(estimated_cost_yuan) FROM analysis_billing_events");
                using (var row = totals.ExecuteReader())
                {
                    Assert.True(row.Read()); Assert.Equal(2L, row.GetInt64(0));
                    Assert.Equal(40L, row.GetInt64(1)); Assert.Equal(60L, row.GetInt64(2));
                    Assert.Equal(1m, row.GetDecimal(3));
                }
                var requestIds = new List<string>();
                using (var ids = upgraded.Command("SELECT request_id FROM analysis_billing_events ORDER BY request_id"))
                using (var reader = ids.ExecuteReader()) while (reader.Read()) requestIds.Add(reader.GetString(0));
                if (restart == 0) initialRequestIds = requestIds.ToArray();
                else Assert.Equal(initialRequestIds, requestIds);
                var columns = new List<string>();
                using (var schema = upgraded.Command("PRAGMA table_info(analysis_billing_events)"))
                using (var reader = schema.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(1));
                Assert.Equal(new[] { "request_id", "provider", "model_version", "input_tokens", "output_tokens", "estimated_cost_yuan", "recorded_utc" }, columns);
                using var foreignKeys = upgraded.Command("PRAGMA foreign_key_list(analysis_billing_events)");
                using var references = foreignKeys.ExecuteReader(); Assert.False(references.Read());
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class Analyzer : ICloudVisionAnalyzer
    {
        public string Provider => "migration-test";
        public string ModelVersion => "test-v1";
        public string PromptVersion => "test-v1";
        public Task<CloudVisionResult> AnalyzeAsync(CloudVisionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
