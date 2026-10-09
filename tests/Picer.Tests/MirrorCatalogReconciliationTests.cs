using Picer.Core.Data;
using Picer.Core.Gateway;

namespace Picer.Tests;

public sealed class MirrorCatalogReconciliationTests
{
    [Fact]
    public void CatalogDeletionRequiresAuthorityAndKeepsPendingButNotAcknowledgedRegisters()
    {
        using var db = new PicerDatabase(":memory:");
        using (var seed = db.Command("""
            INSERT INTO tags(id,name,created_utc) VALUES(1,'pending','2026-10-09'),(2,'acknowledged','2026-10-09'),(3,'remote','2026-10-09');
            INSERT INTO albums(id,name,created_utc) VALUES(1,'pending','2026-10-09'),(2,'acknowledged','2026-10-09'),(3,'remote','2026-10-09');
            INSERT INTO people(id,display_name,created_utc,updated_utc) VALUES(1,'pending','2026-10-09','2026-10-09'),(2,'acknowledged','2026-10-09','2026-10-09'),(3,'remote','2026-10-09','2026-10-09');
            """)) seed.ExecuteNonQuery();
        using (var transaction = db.BeginTransaction())
        {
            foreach (var id in new[] { 1L, 2L })
            {
                db.RecordMetadataEdit("person", id, "name", "edited-" + id, transaction);
                db.RecordMetadataEdit("tag-link", id, id.ToString(), "1", transaction);
                db.RecordMetadataEdit("album-link", id, id.ToString(), "1", transaction);
            }
            transaction.Commit();
        }
        var acknowledged = db.GetMetadataEdits().Where(edit => edit.EntityId == 2).Select(edit => edit.OperationId).ToHashSet();
        foreach (var (kind, table) in new[] { ("tag", "tags"), ("album", "albums"), ("person", "people") })
        {
            db.ReconcileMirroredCatalog(kind, [3], authoritative: false, acknowledged);
            using var before = db.Command("SELECT COUNT(*) FROM " + table); Assert.Equal(3L, before.ExecuteScalar());
            db.ReconcileMirroredCatalog(kind, [3], authoritative: true, acknowledged);
            using var after = db.Command("SELECT group_concat(id,',') FROM (SELECT id FROM " + table + " ORDER BY id)");
            Assert.Equal("1,3", after.ExecuteScalar());
        }
        db.ReplayMetadataEdits();
        Assert.All(db.GetMetadataEdits(), edit => Assert.Equal(1, edit.EntityId));
        using var person = db.Command("SELECT display_name FROM people WHERE id=1"); Assert.Equal("edited-1", person.ExecuteScalar());
        using var absent = db.Command("SELECT COUNT(*) FROM people WHERE id=2"); Assert.Equal(0L, absent.ExecuteScalar());
    }

    [Fact]
    public void NewEditAfterAcknowledgementRetainsTargetAndColdStartIsConservative()
    {
        using var db = new PicerDatabase(":memory:");
        using (var seed = db.Command("INSERT INTO people(id,display_name,created_utc,updated_utc) VALUES(1,'fixture','2026-10-09','2026-10-09')")) seed.ExecuteNonQuery();
        using (var transaction = db.BeginTransaction()) { db.RecordMetadataEdit("person", 1, "name", "first", transaction); transaction.Commit(); }
        var oldAcknowledgement = db.GetMetadataEdits().Select(edit => edit.OperationId).ToHashSet();
        using (var transaction = db.BeginTransaction()) { db.RecordMetadataEdit("person", 1, "name", "newer", transaction); transaction.Commit(); }
        db.ReconcileMirroredCatalog("person", [], true, oldAcknowledgement);
        db.ReconcileMirroredCatalog("person", [], true);
        db.ReplayMetadataEdits();
        using var query = db.Command("SELECT display_name FROM people WHERE id=1"); Assert.Equal("newer", query.ExecuteScalar());
    }

    [Fact]
    public void SceneDeletionDistinguishesUnsupportedCollectionAndNonAuthoritativeMirror()
    {
        using var db = new PicerDatabase(":memory:");
        var scene = new GatewaySceneGroupDescription("fixture", "directory", "2026-10-09", "2026-10-09", "[]", "[]", "fixture", "v1", "v1", "scene", "old description", "[]", "accepted", "2026-10-09", "2026-10-09");
        var metadata = new GatewayMetadata([], [], []) with { SceneGroupDescriptions = [scene] };
        db.UpsertMirroredVideoMetadata(metadata);
        db.ReconcileMirroredPhotoMetadata(metadata with { SceneGroupDescriptions = null }, authoritativeCatalog: true);
        db.ReconcileMirroredPhotoMetadata(metadata with { SceneGroupDescriptions = [] });
        using var query = db.Command("SELECT COUNT(*) FROM scene_group_descriptions"); Assert.Equal(1L, query.ExecuteScalar());
        db.ReconcileMirroredPhotoMetadata(metadata with { SceneGroupDescriptions = [] }, authoritativeCatalog: true);
        Assert.Equal(0L, query.ExecuteScalar());
    }
}
