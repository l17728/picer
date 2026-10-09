using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Video;

namespace Picer.Tests;

public sealed class VideoSchemaMigrationTests
{
    [Fact]
    public void Version41MigrationPreservesVideoIdentityAndEvidenceWithoutInventingHash()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-video-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "picer.db");
        try
        {
            long id;
            using (var database = new PicerDatabase(path))
            {
                var media = Path.Combine(root, "video.mp4"); File.WriteAllBytes(media, [1, 2, 3]);
                var repository = new VideoRepository(database);
                id = repository.Upsert(database.AddSource(root).Id, media).Id;
                repository.SaveTranscript(id, "test", "preserved");
            }
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                LegacySchemaFixture.RevertVersion43(connection);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    DROP INDEX idx_videos_sha256;
                    ALTER TABLE videos DROP COLUMN sha256;
                    PRAGMA user_version=41;
                    """;
                command.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Copy(path + suffix, path + suffix + ".backup");
            using (var upgraded = new PicerDatabase(path))
            {
                var repository = new VideoRepository(upgraded);
                var video = Assert.Single(repository.GetAll());
                Assert.Equal(id, video.Id);
                Assert.Null(video.Sha256);
                Assert.Equal("preserved", repository.GetTranscript(id));
                using var version = upgraded.Command("PRAGMA user_version");
                Assert.Equal((long)PicerDatabase.CurrentSchemaVersion, version.ExecuteScalar());
                using var integrity = upgraded.Command("PRAGMA quick_check");
                Assert.Equal("ok", integrity.ExecuteScalar());
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
