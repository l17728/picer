using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Models;
using Picer.Core.People;

namespace Picer.Tests;

public sealed class FaceMetadataCausalTests
{
    [Fact]
    public void RemovingAPersonClearsHistoricalImportedRejectionsWithoutRemovingOtherPeople()
    {
        using var database=CreateDatabase();var people=new PeopleService(database);
        people.MarkNotThisPerson(1,1);people.MarkNotThisPerson(1,2);
        database.AcceptMetadataEdits(database.GetMetadataEdits());
        using(var imported=database.Command("""
            INSERT INTO sync_imported_metadata_edits(publisher_id,operation_id,entity_kind,entity_id,entity_field)
            VALUES('test','old-first','face-rejection',1,'1'),('test','old-second','face-rejection',1,'2');
            DELETE FROM people WHERE id=1;
            """)) imported.ExecuteNonQuery();
        Assert.Equal("2",Assert.Single(database.GetMetadataEdits().Where(edit=>edit.Kind=="face-rejection")).Field);
        using var retained=database.Command("SELECT operation_id FROM sync_imported_metadata_edits");Assert.Equal("old-second",retained.ExecuteScalar());
        Assert.NotNull(people.Get(2));Assert.Single(database.GetAllMirroredFaces());
    }

    [Fact]
    public void MergeDoesNotRejectItsOwnConfirmedTargetPerson()
    {
        using var database=CreateDatabase();var people=new PeopleService(database);
        people.MarkNotThisPerson(1,2);people.ConfirmFace(1,1);people.Merge(1,2);
        using var rejected=database.Command("SELECT COUNT(*) FROM face_person_rejections");Assert.Equal(0L,rejected.ExecuteScalar());
        Assert.Equal(2,Assert.Single(database.GetAllMirroredFaceAssignments()).PersonId);
        var field=Assert.Single(database.GetMetadataEdits().Where(edit=>edit.Kind=="face-rejection"));
        Assert.Equal("2",field.Field);Assert.False(PicerDatabase.ParseFacePersonRejection(field.Value).Rejected);
        database.ReplayMetadataEdits();Assert.Equal(0L,rejected.ExecuteScalar());
    }

    [Fact]
    public void FaceFieldsNotifyOnlyOwnersAndUnchangedPromotionDoesNotNotifyAgain()
    {
        using var database=CreateDatabase();
        using(var owner=database.Command("""
            INSERT INTO sync_publishers(publisher_id,library_id,device_grant_id,space_id,incarnation_id,epoch,state)
            VALUES('owner','library','grant','space','incarnation','epoch','receiving');
            INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id,target_id)
            VALUES('owner','face','80','global-face',1);
            """)) owner.ExecuteNonQuery();
        new PeopleService(database).MarkNotThisPerson(1,1);
        using var notification=database.Command("SELECT revision FROM sync_publisher_notifications WHERE publisher_id='owner'");
        var before=Convert.ToInt64(notification.ExecuteScalar());Assert.True(before>0);
        database.PromoteUnversionedMetadata();Assert.Equal(before,Convert.ToInt64(notification.ExecuteScalar()));
        database.ReplayMetadataEdits();Assert.Equal(before,Convert.ToInt64(notification.ExecuteScalar()));
    }

    [Fact]
    public void RejectionAndLaterConfirmationSynchronizeWithoutRepeatingTheRejectedSuggestion()
    {
        using var source=CreateDatabase();using var server=CreateDatabase();
        var people=new PeopleService(source);
        people.MarkNotThisPerson(1,1);
        var accepted=server.AcceptMetadataEdits(source.GetMetadataEdits());source.MergeMetadataEdits(accepted);
        Assert.False(new PeopleService(server).SuggestFace(1,1,0.99));
        var rejected=source.GetMetadataEdits().Single(edit=>edit.Kind=="face-rejection");
        Assert.True(PicerDatabase.ParseFacePersonRejection(rejected.Value).Rejected);
        people.ConfirmFace(1,1);
        var allowed=source.GetMetadataEdits().Single(edit=>edit.Kind=="face-rejection");
        Assert.False(PicerDatabase.ParseFacePersonRejection(allowed.Value).Rejected);
        Assert.Equal(rejected.ServerVersion,allowed.BaseVersion);
        source.MergeMetadataEdits(server.AcceptMetadataEdits(source.GetMetadataEdits()));
        Assert.Equal(1,Assert.Single(server.GetAllMirroredFaceAssignments()).PersonId);
        using var rejection=server.Command("SELECT COUNT(*) FROM face_person_rejections");Assert.Equal(0L,rejection.ExecuteScalar());
        server.MergeMetadataEdits(accepted);server.ReplayMetadataEdits();Assert.Equal(0L,rejection.ExecuteScalar());
    }

    [Fact]
    public void RejectionsRemainIndependentAcrossPeopleAndArePurgedWithTheFace()
    {
        using var database=CreateDatabase();var people=new PeopleService(database);
        people.MarkNotThisPerson(1,1);people.MarkNotThisPerson(1,2);
        database.AcceptMetadataEdits(database.GetMetadataEdits());
        Assert.Equal(2,database.GetMetadataEdits().Count(edit=>edit.Kind=="face-rejection"));
        people.ConfirmFace(1,1);
        using var rejection=database.Command("SELECT person_id FROM face_person_rejections");Assert.Equal(2L,rejection.ExecuteScalar());
        using(var remove=database.Command("DELETE FROM detected_faces WHERE id=1")) remove.ExecuteNonQuery();
        Assert.Empty(database.GetMetadataEdits());
        using var retained=database.Command("SELECT COUNT(*) FROM metadata_causal_receipts");Assert.Equal(0L,retained.ExecuteScalar());
    }

    [Fact]
    public void ConcurrentConfirmationAndWithdrawalRetainConflictAndReplayWithoutDirtyingTheIndex()
    {
        using var source=CreateDatabase();using var server=CreateDatabase();
        new PeopleService(source).ConfirmFace(1,1);
        var accepted=Assert.Single(server.AcceptMetadataEdits(source.GetMetadataEdits()));source.MergeMetadataEdits([accepted]);
        new PeopleService(source).MoveFaceToUnassigned(1);
        var pending=Assert.Single(source.GetMetadataEdits());Assert.Equal(accepted.ServerVersion,pending.BaseVersion);
        new PeopleService(server).MoveFace(1,2);
        var changed=server.AcceptMetadataEdits(server.GetMetadataEdits()).Single(edit=>edit.Kind=="face-state");
        server.AcceptMetadataEdits([pending]);source.MergeMetadataEdits([changed]);
        var snapshot=server.GetMetadataConflictSnapshot();var review=Assert.Single(snapshot.Reviews);
        Assert.Equal("移回待分类",review.ProposedDisplay);
        Assert.Equal("确认人物：第二个测试人物",review.CurrentDisplay);
        server.ResolveMetadataEditConflict(pending.OperationId,true,changed.ServerVersion,changed.OperationId,true,snapshot.Epoch);
        var resolved=server.GetMetadataEdits().Single(edit=>edit.Kind=="face-state");source.MergeMetadataEdits([resolved]);
        Assert.Empty(source.GetAllMirroredFaceAssignments());Assert.Empty(server.GetAllMirroredFaceAssignments());
        using var revision=server.Command("SELECT metadata_revision FROM mirror_revision WHERE id=1");var before=revision.ExecuteScalar();
        server.MergeMetadataEdits([resolved]);server.ReplayMetadataEdits();Assert.Equal(before,revision.ExecuteScalar());
        using(var marker=server.Command("INSERT INTO sync_imported_metadata_edits(publisher_id,operation_id,entity_kind,entity_id) VALUES('test',$op,'face-state',1)"))
        { marker.Parameters.AddWithValue("$op",resolved.OperationId);marker.ExecuteNonQuery(); }
        using(var remove=server.Command("DELETE FROM detected_faces WHERE id=1")) remove.ExecuteNonQuery();
        Assert.Empty(server.GetMetadataEdits());Assert.Empty(server.GetMetadataEditConflicts());
        using var retained=server.Command("SELECT (SELECT COUNT(*) FROM metadata_causal_receipts)+(SELECT COUNT(*) FROM sync_imported_metadata_edits)");
        Assert.Equal(0L,retained.ExecuteScalar());
    }

    [Fact]
    public void MergingPeopleRecordsTheMovedManualStateDescriptionAndAlias()
    {
        using var database=CreateDatabase();var people=new PeopleService(database);
        people.ConfirmFace(1,1);people.SetDescription(1,"保留描述");people.AddAlias(1,"旧人物别名");
        people.Merge(1,2);
        var state=PicerDatabase.ParseFaceManualState(database.GetMetadataEdits().Single(edit=>edit.Kind=="face-state").Value);
        Assert.Equal(2,state.PersonId);
        Assert.Contains(database.GetMetadataEdits(),edit=>edit.Kind=="person"&&edit.EntityId==2&&edit.Field=="description"&&edit.Value=="保留描述");
        Assert.Contains(database.GetMetadataEdits(),edit=>edit.Kind=="person"&&edit.EntityId==2&&edit.Value=="旧人物别名");
        database.ReplayMetadataEdits();Assert.Equal(2,Assert.Single(database.GetAllMirroredFaceAssignments()).PersonId);
    }

    [Fact]
    public void LegacyConfirmedFaceMigratesWithCanonicalPayloadAndCanBeRetried()
    {
        var root=Path.Combine(Path.GetTempPath(),"picer-face-state-migration-"+Guid.NewGuid().ToString("N"));var path=Path.Combine(root,"picer.db");
        try
        {
            using(var legacy=CreateDatabase(path))
            {
                legacy.UpsertMirroredFaceAssignment(new(1,1,"confirmed",null,"user",DateTime.UtcNow.ToString("O")));
                using(var connection=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+path))
                {
                    connection.Open();LegacySchemaFixture.RevertPhotoFaceCacheOwnership(connection);
                }
                using var downgrade=legacy.Command("""
                    DROP TRIGGER space_sync_face_manual_purge;
                    DROP TRIGGER space_sync_face_rejection_purge;
                    DROP TRIGGER space_sync_person_rejection_purge;
                    DROP TRIGGER space_sync_video_face_field_purge;
                    DROP TRIGGER space_sync_video_person_rejection_purge;
                    ALTER TABLE sync_imported_metadata_edits DROP COLUMN entity_kind;
                    ALTER TABLE sync_imported_metadata_edits DROP COLUMN entity_id;
                    PRAGMA user_version=68;
                    """);downgrade.ExecuteNonQuery();
            }
            using var migrated=new PicerDatabase(path);
            var state=Assert.Single(migrated.GetMetadataEdits());
            Assert.Equal("face-state",state.Kind);Assert.Equal("测试人脸模型",PicerDatabase.ParseFaceManualState(state.Value).ModelVersion);
            var accepted=Assert.Single(migrated.AcceptMetadataEdits([state]));Assert.True(accepted.ServerVersion>0);
            Assert.Equal(accepted,Assert.Single(migrated.AcceptMetadataEdits([state])));Assert.Empty(migrated.GetMetadataEditConflicts());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(root)) Directory.Delete(root,true); }
    }

    private static PicerDatabase CreateDatabase(string path=":memory:")
    {
        var database=new PicerDatabase(path);var source=database.AddSource(@"C:\Photos");
        database.UpsertMirroredImage(new ImageRecord { Id=1,SourceId=source.Id,Path=@"C:\Photos\one.jpg",FileSize=10,
            ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow });
        database.UpsertMirroredFace(new(1,1,"测试人脸模型",0,0,0,10,10,0.9,null,null,DateTime.UtcNow.ToString("O")));
        database.UpsertMirroredPerson(new(1,"测试人物",0,0));database.UpsertMirroredPerson(new(2,"第二个测试人物",0,0));
        return database;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnOldAssignmentSnapshotCannotUndoAnExplicitManualWithdrawal(bool ignored)
    {
        using var database=new PicerDatabase(":memory:");
        var source=database.AddSource(@"C:\Photos");
        database.UpsertMirroredImage(new ImageRecord { Id=1,SourceId=source.Id,Path=@"C:\Photos\one.jpg",FileSize=10,
            ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow });
        database.UpsertMirroredFace(new(1,1,"face-model",0,0,0,10,10,0.9,null,null,DateTime.UtcNow.ToString("O")));
        database.UpsertMirroredPerson(new(1,"测试人物",0,0));
        var people=new PeopleService(database);
        people.ConfirmFace(1,1);
        var stale=new GatewayFaceAssignment(1,1,"confirmed",null,"user","2030-01-01T00:00:00.0000000Z");
        if(ignored) people.IgnoreFace(1); else people.MoveFaceToUnassigned(1);
        using(var transaction=database.BeginTransaction())
        {
            database.UpsertMirroredFaceAssignment(stale,transaction);
            database.MergeFaceReviewDecisionsInTransaction([],transaction);
            database.ReplayMetadataEditsInTransaction(transaction);
            transaction.Commit();
        }
        using var assignment=database.Command("SELECT COUNT(*) FROM face_person_assignments WHERE face_id=1");
        Assert.Equal(0L,assignment.ExecuteScalar());
        Assert.Equal(ignored?"ignored":"active",Assert.Single(database.GetFaceReviewDecisions()).State);
    }
}
