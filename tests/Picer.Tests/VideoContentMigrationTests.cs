using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Video;

namespace Picer.Tests;

public sealed class VideoContentMigrationTests
{
    [Fact]
    public void RealVersion43DuplicateReferencesMergeEvidenceAndKeepReviewDecisions()
    {
        var root = Path.Combine(Path.GetTempPath(), "video-content-migrate-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "test.db");
        try
        {
            using (var database = new PicerDatabase(path))
            {
                var source = database.AddSource(root).Id;
                var repository = new VideoRepository(database);
                foreach (var id in new[] { 1L, 2L })
                    repository.UpsertMirrored(new VideoRecord(id, source, Path.Combine(root, id + ".mp4"), 3,
                        DateTime.UnixEpoch, null, 0, 0, null, null, "active") { Sha256 = new string(id == 1 ? 'a' : 'b', 64) }, source);
                repository.SaveTranscript(2, "test", "retained legacy transcript");
            }
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open(); LegacySchemaFixture.RevertVersion44(connection);
                using var seed = connection.CreateCommand();
                seed.CommandText = """
                    UPDATE videos SET sha256=(SELECT sha256 FROM videos WHERE id=1) WHERE id=2;
                    INSERT INTO video_keyframes(id,video_id,timestamp_seconds,path,created_utc)
                        VALUES(11,1,0,'one.jpg','2026-01-01'),(22,2,0,'two.jpg','2026-01-01');
                    INSERT INTO video_detected_faces(id,video_id,keyframe_id,timestamp_seconds,ordinal,model_version,box_x,box_y,box_width,box_height,confidence,assignment_state,created_utc,updated_utc)
                        VALUES(31,1,11,0,0,'test',0,0,1,1,1,'ignored','2026-01-01','2026-01-01'),
                              (32,2,22,0,0,'test',0,0,1,1,1,'ignored','2026-01-02','2026-01-02');
                    """;
                seed.ExecuteNonQuery();
            }
            using var upgraded = new PicerDatabase(path);
            var videos = new VideoRepository(upgraded);
            Assert.Equal(videos.GetContentId(1), videos.GetContentId(2));
            Assert.Equal("retained legacy transcript", videos.GetTranscript(1));
            Assert.Single(videos.GetKeyframes(1, false));
            using var faces = upgraded.Command("SELECT COUNT(*) FROM video_detected_faces WHERE assignment_state='ignored'");
            Assert.Equal(1L, faces.ExecuteScalar());
            using var history = upgraded.Command("SELECT COUNT(*) FROM video_content_decision_history WHERE state='ignored'");
            Assert.Equal(1L, history.ExecuteScalar());
            using var integrity = upgraded.Command("PRAGMA foreign_key_check"); Assert.Null(integrity.ExecuteScalar());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
