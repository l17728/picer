using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class SceneDescriptionMigrationTests
{
    [Fact]
    public void ExistingAcceptedTextGetsCausalBaselineAndIndexedMembership()
    {
        var root=Path.Combine(Path.GetTempPath(),"picer-scene-migration-"+Guid.NewGuid().ToString("N"));
        var path=Path.Combine(root,"picer.db");string fingerprint;
        try
        {
            using(var database=new PicerDatabase(path))
            {
                var source=database.AddSource(root);
                var image=database.UpsertImage(new ImageRecord{SourceId=source.Id,Path=Path.Combine(root,"one.jpg"),FileSize=10,ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow});
                var scenes=new SceneGroupDescriptionService(database,new CloudVisionRepository(database));var plan=scenes.BuildPlan(root,[image]);fingerprint=plan.Fingerprint;
                scenes.SaveAccepted(plan,new(fingerprint,"旧场景","保留原描述",["旧场景"],"user","manual","v1","accepted"));
                using var downgrade=database.Command("""
                    DELETE FROM metadata_edits WHERE kind='scene-description';
                    DROP TRIGGER scene_group_member_insert;DROP TRIGGER scene_group_member_update;
                    DROP TRIGGER space_sync_scene_purge;DROP TRIGGER space_sync_scene_group_INSERT;
                    DROP TRIGGER space_sync_scene_group_UPDATE;DROP TRIGGER space_sync_scene_group_DELETE;
                    DROP TABLE scene_group_members;DROP TABLE sync_scene_bindings;
                    PRAGMA user_version=88;
                    """);downgrade.ExecuteNonQuery();
            }
            using(var migrated=new PicerDatabase(path))
            {
                var edit=Assert.Single(migrated.GetMetadataEdits(),item=>item.Kind=="scene-description");
                PicerDatabase.ValidateMetadataEdit(edit);Assert.Equal(fingerprint,edit.Field);Assert.Contains("旧场景",migrated.DescribeMetadataValue(edit));
                using var count=migrated.Command("SELECT COUNT(*) FROM scene_group_members");Assert.Equal(1L,count.ExecuteScalar());
                using var remove=migrated.Command("DELETE FROM scene_group_descriptions");remove.ExecuteNonQuery();
                Assert.Empty(migrated.GetMetadataEdits());Assert.Equal(0L,count.ExecuteScalar());
            }
        }
        finally { SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true); }
    }
}
