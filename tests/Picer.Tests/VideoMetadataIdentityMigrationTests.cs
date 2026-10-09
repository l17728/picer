using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Search;
using Picer.Core.Video;

namespace Picer.Tests;

public sealed class VideoMetadataIdentityMigrationTests
{
    [Fact]
    public void RealVersion44BindsExistingEditsAndNeverBindsMissingReferences()
    {
        var root = Path.Combine(Path.GetTempPath(), "video-edit-migrate-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "test.db");
        try
        {
            long tag;
            using (var db = new PicerDatabase(path))
            {
                var source = db.AddSource(root).Id;
                new VideoRepository(db).UpsertMirrored(new(100, source, Path.Combine(root, "clip.mp4"), 3,
                    DateTime.UnixEpoch, null, 0, 0, null, null, "active") { Sha256 = new string('a', 64) }, source);
                tag = SemanticEngine.AddTag(db, "legacy").Id;
            }
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open(); LegacySchemaFixture.RevertVersion45(connection);
                using var seed = connection.CreateCommand();
                seed.CommandText = """
                    INSERT INTO metadata_edits(kind,entity_id,field,value,updated_utc,operation_id)
                    VALUES('video-tag-link',100,$tag,'1',$utc,'11111111111111111111111111111111'),
                          ('video-tag-link',999,$tag,'1',$utc,'22222222222222222222222222222222');
                    """;
                seed.Parameters.AddWithValue("$tag", tag.ToString()); seed.Parameters.AddWithValue("$utc", DateTime.UnixEpoch.ToString("O"));
                seed.ExecuteNonQuery();
            }
            using var upgraded = new PicerDatabase(path);
            upgraded.ReplayMetadataEdits();
            var edit = Assert.Single(upgraded.GetMetadataEdits());
            Assert.Equal("sha256:" + new string('a', 64), edit.VideoContentIdentity);
            Assert.Contains(tag, new VideoSearchMetadata(upgraded).GetTags(100));
            using var bindings = upgraded.Command("SELECT COUNT(*) FROM video_metadata_operation_bindings");
            Assert.Equal(2L, bindings.ExecuteScalar());
            using var orphan = upgraded.Command("SELECT video_content_identity FROM metadata_edits WHERE entity_id=999");
            Assert.StartsWith("orphan:", (string)orphan.ExecuteScalar()!);
            using var check = upgraded.Command("PRAGMA foreign_key_check"); Assert.Null(check.ExecuteScalar());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
