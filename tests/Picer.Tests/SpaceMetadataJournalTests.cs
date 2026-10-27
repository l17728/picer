using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.Tests;

public sealed class SpaceMetadataJournalTests
{
    [Fact]
    public void FaceJournalOmitsEmbeddingsAndContentRejectionRetainsItsNaturalKey()
    {
        using var database = new PicerDatabase(":memory:");
        var source = database.AddSource(@"C:\Photos");
        database.UpsertMirroredImage(new ImageRecord { Id = 1, SourceId = source.Id,
            Path = @"C:\Photos\one.jpg", FileSize = 10, ModifiedUtc = DateTime.UtcNow,
            IndexedUtc = DateTime.UtcNow });
        Execute(database, """
            INSERT INTO sync_publishers(publisher_id,library_id,incarnation_id,device_grant_id,space_id,state,epoch)
            VALUES('publisher','library','device','grant','space','publishing','epoch');
            INSERT INTO detected_faces(id,image_id,model_version,ordinal,box_x,box_y,box_width,box_height,confidence,embedding,created_utc)
            VALUES(1,1,'model',0,0,0,10,10,0.9,X'010203','2026-10-23');
            UPDATE detected_faces SET embedding=X'040506' WHERE id=1;
            INSERT INTO image_classification_rejections(image_id,content_sha256,concept_id,source_scope,rejected_utc)
            VALUES(NULL,'content-hash','landscape','vision_model','2026-10-23');
            DELETE FROM image_classification_rejections;
            """);
        using (var face = database.Command("SELECT after_payload FROM sync_metadata_row_journal WHERE table_name='detected_faces'"))
        using (var rows = face.ExecuteReader())
        {
            Assert.True(rows.Read());
            using var payload = JsonDocument.Parse(rows.GetString(0));
            Assert.False(payload.RootElement.TryGetProperty("embedding", out _));
            Assert.Equal(1, payload.RootElement.GetProperty("image_id").GetInt32());
            Assert.False(rows.Read());
        }
        using var rejection = database.Command("SELECT row_key FROM sync_metadata_row_journal WHERE operation='delete'");
        using var key = JsonDocument.Parse((string)rejection.ExecuteScalar()!);
        Assert.Equal(JsonValueKind.Null, key.RootElement[0].ValueKind);
        Assert.Equal("content-hash", key.RootElement[1].GetString());
        Assert.Equal("landscape", key.RootElement[2].GetString());
        Assert.Equal("vision_model", key.RootElement[3].GetString());
        database.UpsertMirroredPerson(new(1, "person", 0, 0));
        Execute(database, """
            INSERT INTO face_person_assignments(face_id,person_id,state,assigned_by,updated_utc)
            VALUES(1,1,'confirmed','user','2026-10-23');
            """);
        database.DeleteImage(1);
        using var remaining = database.Command("SELECT COUNT(*) FROM sync_metadata_row_journal WHERE table_name IN ('detected_faces','face_person_assignments')");
        Assert.Equal(0L, remaining.ExecuteScalar());
    }

    [Fact]
    public void Version57UpgradePreservesRowsAndDeleteSurvivesReopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-journal-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "picer.db");
        try
        {
            using (var database = new PicerDatabase(path))
            {
                using (var connection = database.Command("SELECT 1"))
                    LegacySchemaFixture.RevertVideoMetadataOwnership(connection.Connection!);
                var triggers = new List<string>();
                using (var query = database.Command("SELECT name FROM sqlite_master WHERE type='trigger' AND name GLOB 'space_sync_row_*'"))
                using (var rows = query.ExecuteReader())
                    while (rows.Read()) triggers.Add(rows.GetString(0));
                foreach (var trigger in triggers) Execute(database, $"DROP TRIGGER \"{trigger}\"");
                Execute(database, """
                    DROP TABLE sync_metadata_row_journal;
                    INSERT INTO tags(id,name,created_utc) VALUES(900,'existing','2026-10-23');
                    INSERT INTO sync_publishers(publisher_id,library_id,incarnation_id,device_grant_id,space_id,state,epoch)
                    VALUES('publisher','library','device','grant','space','publishing','epoch');
                    PRAGMA user_version=57;
                    """);
            }
            using (var upgraded = new PicerDatabase(path))
            {
                using var existing = upgraded.Command("SELECT name FROM tags WHERE id=900");
                Assert.Equal("existing", existing.ExecuteScalar());
                Execute(upgraded, "DELETE FROM tags WHERE id=900");
            }
            using var reopened = new PicerDatabase(path);
            using var deleted = reopened.Command("SELECT operation FROM sync_metadata_row_journal WHERE table_name='tags'");
            Assert.Equal("delete", deleted.ExecuteScalar());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void JournalRetainsExplicitDeleteAndRollsBackWithBusinessWrite()
    {
        using var database = new PicerDatabase(":memory:");
        Execute(database, """
            INSERT INTO sync_publishers(publisher_id,library_id,incarnation_id,device_grant_id,space_id,state,epoch)
            VALUES('publisher','library','device','grant','space','publishing','epoch');
            INSERT INTO tags(id,name,created_utc) VALUES(900,'first','2026-10-23');
            UPDATE tags SET name='second' WHERE id=900;
            UPDATE tags SET name='second' WHERE id=900;
            DELETE FROM tags WHERE id=900;
            """);
        using (var read = database.Command("SELECT operation,before_payload,after_payload FROM sync_metadata_row_journal ORDER BY sequence"))
        using (var rows = read.ExecuteReader())
        {
            Assert.True(rows.Read()); Assert.Equal("upsert", rows.GetString(0)); Assert.True(rows.IsDBNull(1));
            Assert.True(rows.Read()); Assert.Equal("upsert", rows.GetString(0));
            Assert.True(rows.Read()); Assert.Equal("delete", rows.GetString(0)); Assert.True(rows.IsDBNull(2));
            using var deleted = JsonDocument.Parse(rows.GetString(1));
            Assert.Equal("second", deleted.RootElement.GetProperty("name").GetString());
            Assert.False(rows.Read());
        }
        using (var transaction = database.BeginTransaction())
        {
            using var write = database.Command("INSERT INTO tags(id,name,created_utc) VALUES(901,'rollback','2026-10-23')");
            write.Transaction = transaction; write.ExecuteNonQuery();
            transaction.Rollback();
        }
        using var count = database.Command("SELECT COUNT(*) FROM sync_metadata_row_journal");
        Assert.Equal(3L, count.ExecuteScalar());
    }

    [Fact]
    public void UnconfiguredLibraryAndRemoteApplicationDoNotProduceJournalEcho()
    {
        using var database = new PicerDatabase(":memory:");
        Execute(database, """
            INSERT INTO tags(id,name,created_utc) VALUES(900,'offline','2026-10-23');
            INSERT INTO sync_publishers(publisher_id,library_id,incarnation_id,device_grant_id,space_id,state,epoch)
            VALUES('publisher','library','device','grant','space','publishing','epoch');
            INSERT INTO sync_suppression(id) VALUES(1);
            UPDATE tags SET name='remote' WHERE id=900;
            DELETE FROM sync_suppression WHERE id=1;
            """);
        using var count = database.Command("SELECT COUNT(*) FROM sync_metadata_row_journal");
        Assert.Equal(0L, count.ExecuteScalar());
        Execute(database, "UPDATE tags SET name='local' WHERE id=900");
        Assert.Equal(1L, count.ExecuteScalar());
    }

    private static void Execute(PicerDatabase database, string sql)
    {
        using var command = database.Command(sql);
        command.ExecuteNonQuery();
    }
}
