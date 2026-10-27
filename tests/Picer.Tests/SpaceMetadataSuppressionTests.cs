using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.Tests;

public sealed class SpaceMetadataSuppressionTests
{
    [Fact]
    public void Version56UpgradeSuppressesImportedLocationButTracksNextLocalEdit()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-suppression-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "picer.db");
        try
        {
            using (var old = new PicerDatabase(path))
            {
                var source = old.AddSource(@"C:\Photos");
                old.UpsertMirroredImage(new ImageRecord { Id = 1, SourceId = source.Id, Path = @"C:\Photos\one.jpg",
                    FileSize = 10, ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow });
                using var downgradeFixture = old.Command("""
                    DROP TRIGGER mirror_metadata_image_locations_INSERT;
                    CREATE TRIGGER mirror_metadata_image_locations_INSERT AFTER INSERT ON image_locations
                    BEGIN UPDATE mirror_revision SET metadata_revision=metadata_revision+1 WHERE id=1; END;
                    PRAGMA user_version=56;
                    """);
                LegacySchemaFixture.RevertVideoMetadataOwnership(downgradeFixture.Connection!);
                downgradeFixture.ExecuteNonQuery();
            }
            using var upgraded = new PicerDatabase(path);
            using (var version = upgraded.Command("PRAGMA user_version")) Assert.Equal((long)PicerDatabase.CurrentSchemaVersion, version.ExecuteScalar());
            long before;
            using (var revision = upgraded.Command("SELECT metadata_revision FROM mirror_revision WHERE id=1")) before = Convert.ToInt64(revision.ExecuteScalar());
            var location = new ImageLocationHierarchyRow(1, false, "country", null, "remote-city", null, null, null, null);
            using (var transaction = upgraded.BeginTransaction())
            {
                using var suppress = upgraded.Command("INSERT INTO sync_suppression(id) VALUES(1)");
                suppress.Transaction = transaction; suppress.ExecuteNonQuery();
                upgraded.UpsertMirroredLocation(location, transaction);
                using var resume = upgraded.Command("DELETE FROM sync_suppression WHERE id=1");
                resume.Transaction = transaction; resume.ExecuteNonQuery();
                transaction.Commit();
            }
            using (var revision = upgraded.Command("SELECT metadata_revision FROM mirror_revision WHERE id=1")) Assert.Equal(before, revision.ExecuteScalar());
            upgraded.UpsertMirroredLocation(location with { City = "local-city" });
            using (var revision = upgraded.Command("SELECT metadata_revision FROM mirror_revision WHERE id=1")) Assert.True(Convert.ToInt64(revision.ExecuteScalar()) > before);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
