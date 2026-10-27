using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Identity;
using Picer.Core.Models;
using Picer.Core.Video;
using Picer.Core.Search;
using Picer.Core.People;
using Picer.Core.Gateway;
using Picer.IndexService.Identity;
using Xunit;

namespace Picer.Tests;

public sealed class SpaceSyncReceiverTests : IDisposable
{
    [Theory]
    [InlineData("catalog","{\"tags\":[null],\"albums\":[],\"people\":[]}")]
    [InlineData("catalog","{\"tags\":[],\"albums\":[null],\"people\":[]}")]
    [InlineData("catalog","{\"tags\":[],\"albums\":[],\"people\":[null]}")]
    [InlineData("metadata-edits","{\"edits\":[null]}")]
    public void NullCatalogEntriesAreRejectedAndReplayTheirFailureReceipt(string kind,string payload)
    {
        var source=Publisher(Guid.NewGuid().ToString("N"));
        using var document=JsonDocument.Parse(payload);
        var batch=new SpaceSyncBatch(source.Publisher.PublisherId,source.Publisher.Epoch,1,Guid.NewGuid().ToString("N"),kind,document.RootElement.Clone(),[]);
        var receipt=source.Receiver.Apply(batch);Assert.Equal(422,receipt.Status);
        Assert.Equal(receipt,source.Receiver.Apply(batch));
    }

    [Fact]
    public void OldServiceProtocolRequiresUpgradeInsteadOfReceivingAnUnsupportedBaseline()
    {
        var publisher=new SpacePublisher(Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),"device",Guid.NewGuid().ToString("N"),0);
        Assert.Throws<InvalidOperationException>(()=>SpaceSyncProtocol.RequireMetadataSupport(publisher));
        SpaceSyncProtocol.RequireMetadataSupport(publisher with { MetadataProtocol=SpaceSyncProtocol.MetadataVersion });
    }

    [Fact]
    public void ManualMetadataPagesExceedTwentyThousandAndChangesAfterTheCutArriveNext()
    {
        var source=Publisher(Guid.NewGuid().ToString("N")); var other=Publisher(Guid.NewGuid().ToString("N"));
        var catalog=new SpaceCatalogBatch([],[],[new(1,"分页人物",0,0)]);
        Assert.Equal(200,source.Receiver.Apply(new(source.Publisher.PublisherId,source.Publisher.Epoch,1,Guid.NewGuid().ToString("N"),"catalog",
            JsonSerializer.SerializeToElement(catalog,DeviceAuthorization.Json),[new("person",1,Guid.NewGuid().ToString("N"))])).Status);
        using var identity=_database.Command("SELECT target_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind='person'");
        identity.Parameters.AddWithValue("$publisher",source.Publisher.PublisherId); var person=Convert.ToInt64(identity.ExecuteScalar());
        const int aliases=20_005;
        using (var transaction=_database.BeginTransaction())
        using (var seed=_database.Command("INSERT INTO metadata_edits(kind,entity_id,field,value,updated_utc,operation_id) VALUES('person',$person,$field,$value,$utc,$op)"))
        {
            seed.Transaction=transaction;
            for (var index=0;index<=aliases;index++)
            {
                var value="分页别名"+index;
                seed.Parameters.Clear(); seed.Parameters.AddWithValue("$person",person);
                seed.Parameters.AddWithValue("$field",index==aliases?"description":PicerDatabase.AliasField(value)); seed.Parameters.AddWithValue("$value",value);
                seed.Parameters.AddWithValue("$utc",DateTime.UtcNow.ToString("O")); seed.Parameters.AddWithValue("$op",Guid.NewGuid().ToString("N")); seed.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        var page=source.Receiver.ReadMetadataEditPage(0,"",null,500,source.Publisher.Epoch);
        Assert.True(page.HasMore); Assert.Equal(500,page.Edits.Count); var cut=page.Through;
        var description=_database.GetMetadataEdits().Single(edit=>edit.Field=="description");
        var latest=Assert.Single(_database.AcceptMetadataEdits([description with { Value="分页期间的新描述",OperationId=Guid.NewGuid().ToString("N"),ServerVersion=0,BaseVersion=description.ServerVersion }]));
        var received=new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            Assert.All(page.Edits,edit=> { Assert.Equal(1,edit.EntityId); Assert.True(edit.ServerVersion<=cut); Assert.True(received.Add(edit.OperationId)); });
            if (!page.HasMore) break;
            page=source.Receiver.ReadMetadataEditPage(page.NextVersion,page.NextOperation,cut,500,source.Publisher.Epoch);
        }
        Assert.Equal(aliases,received.Count); Assert.Equal(cut,page.NextVersion); Assert.Equal("~",page.NextOperation);
        var delta=source.Receiver.ReadMetadataEditPage(page.NextVersion,page.NextOperation,null,500,source.Publisher.Epoch);
        Assert.Equal(latest.OperationId,Assert.Single(delta.Edits).OperationId);
        Assert.Empty(source.Receiver.ReadMetadataEditPage(delta.NextVersion,delta.NextOperation,null,500,source.Publisher.Epoch).Edits);
        Assert.Empty(other.Receiver.ReadMetadataEditPage(0,"",null,500,other.Publisher.Epoch).Edits);
        Assert.Throws<InvalidOperationException>(()=>source.Receiver.ReadMetadataEditPage(0,"",latest.ServerVersion+1,500,source.Publisher.Epoch));
        Assert.Throws<InvalidOperationException>(()=>source.Receiver.ReadMetadataEditPage(0,"",null,500,Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void AccountPreferencesNotifyAllSourcesWithoutExposingAnotherSourcesMedia()
    {
        var a=Publisher(Guid.NewGuid().ToString("N")); var b=Publisher(Guid.NewGuid().ToString("N"));
        Assert.Equal(200,a.Receiver.Apply(Images(a.Publisher)).Status);
        var before=b.Receiver.ReadNotification(new(b.Publisher.LibraryId)).Revision;
        var edit=new MetadataEdit("space-setting",1,"clustering",JsonSerializer.Serialize(new ClusterPreference(false,0.76f)),DateTime.UtcNow.ToString("O"),Guid.NewGuid().ToString("N"));
        Assert.Equal(200,a.Receiver.Apply(new(a.Publisher.PublisherId,a.Publisher.Epoch,2,Guid.NewGuid().ToString("N"),"metadata-edits",
            JsonSerializer.SerializeToElement(new SpaceMetadataEditsBatch([edit]),DeviceAuthorization.Json),[])).Status);
        Assert.True(b.Receiver.ReadNotification(new(b.Publisher.LibraryId)).Revision>before);
        var preference=Assert.Single(b.Receiver.ReadMetadataEdits().Edits); Assert.Equal("space-setting",preference.Kind);
        Assert.Empty(b.Receiver.ReadMetadataChanges(0,100).Changes);
        var revision=b.Receiver.ReadNotification(new(b.Publisher.LibraryId)).Revision;
        var settings=new Picer.Core.Vision.ContentSemanticClusterService(_database);
        settings.SaveSettings(false,0.76f,true,preference.ServerVersion);
        Assert.True(b.Receiver.ReadNotification(new(b.Publisher.LibraryId)).Revision>revision);
        Assert.True(Assert.Single(b.Receiver.ReadMetadataEdits().Edits).ServerVersion>preference.ServerVersion);
        var state=Assert.Single(a.Receiver.ReadMetadataOperationStates(new(a.Publisher.PublisherId,a.Publisher.Epoch,[edit.OperationId])).Operations);
        Assert.Equal(edit.OperationId,state.OperationId); Assert.True(state.Current.ServerVersion>preference.ServerVersion);
        Assert.Empty(b.Receiver.ReadMetadataOperationStates(new(b.Publisher.PublisherId,b.Publisher.Epoch,[edit.OperationId])).Operations);
        Assert.Throws<UnauthorizedAccessException>(()=>b.Receiver.ReadMetadataOperationStates(new(a.Publisher.PublisherId,a.Publisher.Epoch,[edit.OperationId])));
    }

    [Fact]
    public void ConflictReceiptsArePublisherScopedEpochFencedAndOnlyExposeResolvedVersions()
    {
        var source=Publisher(Guid.NewGuid().ToString("N")); var other=Publisher(Guid.NewGuid().ToString("N"));
        _database.UpsertMirroredPerson(new(1,"initial",0,0));
        var operation=Guid.NewGuid().ToString("N");
        var remoteOperation=DeviceAuthorization.Digest(source.Publisher.PublisherId+":"+operation)[..32];
        var edit=new MetadataEdit("person",1,"name","current",DateTime.UtcNow.ToString("O"),Guid.NewGuid().ToString("N"));
        var current=Assert.Single(_database.AcceptMetadataEdits([edit]));
        _database.AcceptMetadataEdits([edit with { OperationId=remoteOperation,Value="proposed" }]);
        var request=new SpaceArchivedOperationRequest(source.Publisher.PublisherId,source.Publisher.Epoch,[operation]);
        Assert.Empty(source.Receiver.ReadConflictResolutions(request).Resolutions);
        _database.ResolveMetadataEditConflict(remoteOperation,true,current.ServerVersion,current.OperationId,true,source.Publisher.Epoch);
        var receipt=Assert.Single(source.Receiver.ReadConflictResolutions(request).Resolutions);
        Assert.Equal(operation,receipt.OperationId); Assert.True(receipt.Version>current.ServerVersion);
        Assert.Empty(other.Receiver.ReadConflictResolutions(new(other.Publisher.PublisherId,other.Publisher.Epoch,[operation,remoteOperation])).Resolutions);
        Assert.Throws<UnauthorizedAccessException>(()=>other.Receiver.ReadConflictResolutions(request));
        Assert.Throws<InvalidOperationException>(()=>source.Receiver.ReadConflictResolutions(request with { Epoch=Guid.NewGuid().ToString("N") }));
        Assert.Throws<ArgumentException>(()=>source.Receiver.ReadConflictResolutions(request with { OperationIds=["invalid"] }));
    }

    [Fact]
    public void SpaceInspectionCannotCompleteWhileCentralManualConflictsRemain()
    {
        _database.UpsertMirroredPerson(new(1,"initial",0,0));
        var edit=new MetadataEdit("person",1,"name","current",DateTime.UtcNow.ToString("O"),Guid.NewGuid().ToString("N"));
        _database.AcceptMetadataEdits([edit]);
        _database.AcceptMetadataEdits([edit with { Value="proposed",OperationId=Guid.NewGuid().ToString("N") }]);
        var receiver=new SpaceSyncReceiver(_database,Device());
        var job=receiver.CreateInspection(new(Guid.NewGuid().ToString("N")));
        Assert.Equal("pending_review",job.State); Assert.Equal(1,job.PendingMetadataConflicts);
        var snapshot=_database.GetMetadataConflictSnapshot(); var conflict=Assert.Single(snapshot.Reviews).Conflict;
        _database.ResolveMetadataEditConflict(conflict.OperationId,false,conflict.Current.ServerVersion,conflict.Current.OperationId,
            authoritative:true,expectedEpoch:snapshot.Epoch);
        var completed=receiver.ReadInspection(new(job.JobId));
        Assert.Equal("completed",completed.State); Assert.Equal(0,completed.PendingMetadataConflicts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-an-operation-id")]
    public void SourceOperationIsValidatedBeforeNamespacingItsId(string? operationId)
    {
        var source = Publisher(Guid.NewGuid().ToString("N"));
        var video = new VideoRecord(1, 1, @"C:\Photos\clip.mp4", 10, DateTime.UnixEpoch, null, 0, 0, null, null, "active") { Sha256 = new string('f', 64) };
        Assert.Equal(200, source.Receiver.Apply(new(source.Publisher.PublisherId, source.Publisher.Epoch, 1, Guid.NewGuid().ToString("N"), "videos",
            JsonSerializer.SerializeToElement(new SpaceVideoBatch(1, @"C:\Photos", [video]), DeviceAuthorization.Json), [new("video", 1, Guid.NewGuid().ToString("N"))])).Status);
        var edit = new MetadataEdit("video-concept", 1, "[\"animal\",\"manual\"]", "accepted", DateTime.UtcNow.ToString("O"), operationId!)
            { VideoContentIdentity = "sha256:" + new string('f', 64) };
        var receipt = source.Receiver.Apply(new(source.Publisher.PublisherId, source.Publisher.Epoch, 2, Guid.NewGuid().ToString("N"), "metadata-edits",
            JsonSerializer.SerializeToElement(new SpaceMetadataEditsBatch([edit]), DeviceAuthorization.Json), []));
        Assert.Equal(422, receipt.Status);
        Assert.Empty(_database.GetMetadataEdits());
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-space-sync-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _database;
    public SpaceSyncReceiverTests() => _database = new(Path.Combine(_root, "picer.db"));
    private static AuthorizedDevice Device() => new(Guid.NewGuid().ToString("N"), "account", "space", Guid.NewGuid().ToString("N"), "public", "gui", AccountStore.EditorScopes, 1, 1);
    private (SpaceSyncReceiver Receiver, SpacePublisher Publisher, AuthorizedDevice Device) Publisher(string library)
    {
        var device = Device();
        var receiver = new SpaceSyncReceiver(_database, device);
        return (receiver, receiver.Register(new(library, device.DeviceId)), device);
    }
    private static SpaceSyncBatch Images(SpacePublisher publisher, long sequence = 1, string path = @"C:\Photos\same.jpg") =>
        new(publisher.PublisherId, publisher.Epoch, sequence, Guid.NewGuid().ToString("N"), "images",
            JsonSerializer.SerializeToElement(new SpaceImageBatch(1, @"C:\Photos", [new ImageRecord
            {
                Id = 1, SourceId = 1, Path = path, FileSize = 100, Sha256 = new string('a', 64), ContentSha256 = new string('a', 64),
                ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow
            }]), DeviceAuthorization.Json), [new("image", 1, Guid.NewGuid().ToString("N"))]);

    [Fact]
    public void UnhashedVideoManualDecisionsRoundTripThroughPublisherContentIdentity()
    {
        using var local = new PicerDatabase(Path.Combine(_root, "unhashed-source.db"));
        var source = local.AddSource(@"C:\Photos").Id;
        var video = new VideoRecord(1, source, @"C:\Photos\clip.mp4", 10, DateTime.UnixEpoch, null, 0, 0, null, null, "active");
        new VideoRepository(local).UpsertRemote(video, source, preserveIdentity: true);
        var identity = local.GetLocalVideoContentIdentity(1);
        var owner = Publisher(Guid.NewGuid().ToString("N"));
        var other = Publisher(Guid.NewGuid().ToString("N"));
        SpaceSyncBatch Videos(SpacePublisher publisher) => new(publisher.PublisherId, publisher.Epoch, 1, Guid.NewGuid().ToString("N"), "videos",
            JsonSerializer.SerializeToElement(new SpaceVideoBatch(source, @"C:\Photos", [video], new Dictionary<long,string> { [1]=identity }), DeviceAuthorization.Json),
            [new("video", 1, Guid.NewGuid().ToString("N"))]);
        Assert.Equal(200, owner.Receiver.Apply(Videos(owner.Publisher)).Status);
        Assert.Equal(200, other.Receiver.Apply(Videos(other.Publisher)).Status);
        using (var transaction = local.BeginTransaction())
        {
            local.RecordMetadataEdit("video-concept", 1, "[\"animal\",\"manual\"]", "accepted", transaction);
            transaction.Commit();
        }
        var edit = Assert.Single(local.GetMetadataEdits());
        Assert.Equal(identity, edit.VideoContentIdentity);
        var upload = new SpaceSyncBatch(owner.Publisher.PublisherId, owner.Publisher.Epoch, 2, Guid.NewGuid().ToString("N"), "metadata-edits",
            JsonSerializer.SerializeToElement(new SpaceMetadataEditsBatch([edit]), DeviceAuthorization.Json), []);
        Assert.Equal(200, owner.Receiver.Apply(upload).Status);
        var received = Assert.Single(owner.Receiver.ReadMetadataEditPage(0, "", null, 500, owner.Publisher.Epoch).Edits);
        Assert.Equal(identity, received.VideoContentIdentity);
        Assert.Equal("accepted", received.Value);
        Assert.True(received.ServerVersion > 0);
        local.MergeMetadataEdits([received]);
        Assert.Equal(received.ServerVersion, Assert.Single(local.GetMetadataEdits()).ServerVersion);
        Assert.Empty(other.Receiver.ReadMetadataEditPage(0, "", null, 500, other.Publisher.Epoch).Edits);
        Assert.Equal(200, owner.Receiver.Apply(upload with { Sequence=3, OperationId=Guid.NewGuid().ToString("N") }).Status);
        Assert.Single(owner.Receiver.ReadMetadataEdits().Edits);
        var replacementIdentity = "local:" + Guid.NewGuid().ToString("N") + ":2";
        Assert.Equal(200, owner.Receiver.Apply(new(owner.Publisher.PublisherId, owner.Publisher.Epoch, 4, Guid.NewGuid().ToString("N"), "videos",
            JsonSerializer.SerializeToElement(new SpaceVideoBatch(source, @"C:\Photos", [video with { FileSize=20 }],
                new Dictionary<long,string> { [1]=replacementIdentity }), DeviceAuthorization.Json),
            [new("video", 1, VideosIdentity())])).Status);
        Assert.Empty(owner.Receiver.ReadMetadataEdits().Edits);
        Assert.Equal(422, owner.Receiver.Apply(upload with { Sequence=5, OperationId=Guid.NewGuid().ToString("N") }).Status);
        Assert.Empty(owner.Receiver.ReadMetadataEdits().Edits);

        string VideosIdentity()
        {
            using var query = _database.Command("SELECT global_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind='video' AND local_key='1'");
            query.Parameters.AddWithValue("$publisher", owner.Publisher.PublisherId);
            return (string)query.ExecuteScalar()!;
        }
    }

    [Fact]
    public void UnknownVideoContentIdentityIsRejectedInsteadOfSilentlyAcknowledged()
    {
        var owner = Publisher(Guid.NewGuid().ToString("N"));
        var video = new VideoRecord(1, 1, @"C:\Photos\clip.mp4", 10, DateTime.UnixEpoch, null, 0, 0, null, null, "active");
        Assert.Equal(200, owner.Receiver.Apply(new(owner.Publisher.PublisherId, owner.Publisher.Epoch, 1, Guid.NewGuid().ToString("N"), "videos",
            JsonSerializer.SerializeToElement(new SpaceVideoBatch(1, @"C:\Photos", [video]), DeviceAuthorization.Json),
            [new("video", 1, Guid.NewGuid().ToString("N"))])).Status);
        var edit = new MetadataEdit("video-concept", 1, "[\"animal\",\"manual\"]", "accepted", DateTime.UtcNow.ToString("O"), Guid.NewGuid().ToString("N"))
            { VideoContentIdentity="local:"+Guid.NewGuid().ToString("N")+":1" };
        var result = owner.Receiver.Apply(new(owner.Publisher.PublisherId, owner.Publisher.Epoch, 2, Guid.NewGuid().ToString("N"), "metadata-edits",
            JsonSerializer.SerializeToElement(new SpaceMetadataEditsBatch([edit]), DeviceAuthorization.Json), []));
        Assert.Equal(422, result.Status);
        Assert.Equal(0, result.Accepted);
        Assert.Empty(_database.GetMetadataEdits());
    }

    [Fact]
    public void SameVideoHashDoesNotAuthorizeDeliveryToAnotherPublisher()
    {
        var first=Publisher(Guid.NewGuid().ToString("N")); var second=Publisher(Guid.NewGuid().ToString("N"));
        var unrelated=Publisher(Guid.NewGuid().ToString("N"));
        foreach (var (owner,hash) in new[]{(first,'a'),(second,'a'),(unrelated,'b')})
        {
            var video=new VideoRecord(1,1,@"C:\Photos\clip.mp4",10,DateTime.UnixEpoch,null,0,0,null,null,"active") { Sha256=new string(hash,64) };
            Assert.Equal(200,owner.Receiver.Apply(new(owner.Publisher.PublisherId,owner.Publisher.Epoch,1,Guid.NewGuid().ToString("N"),"videos",
                JsonSerializer.SerializeToElement(new SpaceVideoBatch(1,@"C:\Photos",[video]),DeviceAuthorization.Json),[new("video",1,Guid.NewGuid().ToString("N"))])).Status);
        }
        var before=second.Receiver.ReadNotification(new(second.Publisher.LibraryId)).Revision;
        var otherBefore=unrelated.Receiver.ReadNotification(new(unrelated.Publisher.LibraryId)).Revision;
        var operation=new MetadataEdit("video-concept",1,"[\"animal\",\"manual\"]","accepted",DateTime.UnixEpoch.ToString("O"),Guid.NewGuid().ToString("N"))
            { VideoContentIdentity="sha256:"+new string('a',64) };
        var batch=new SpaceSyncBatch(first.Publisher.PublisherId,first.Publisher.Epoch,2,Guid.NewGuid().ToString("N"),"metadata-edits",
            JsonSerializer.SerializeToElement(new SpaceMetadataEditsBatch([operation]),DeviceAuthorization.Json),[]);
        Assert.Equal(200,first.Receiver.Apply(batch).Status);
        var notified=second.Receiver.ReadNotification(new(second.Publisher.LibraryId)).Revision;
        Assert.Equal(before,notified);
        Assert.Equal(otherBefore,unrelated.Receiver.ReadNotification(new(unrelated.Publisher.LibraryId)).Revision);
        var own=Assert.Single(first.Receiver.ReadMetadataEdits().Edits);
        Assert.Equal(1,own.EntityId); Assert.Equal("accepted",own.Value); Assert.True(own.ServerVersion>0);
        Assert.Empty(second.Receiver.ReadMetadataEdits().Edits);
        Assert.Empty(unrelated.Receiver.ReadMetadataEdits().Edits);
        Assert.Equal(200,first.Receiver.Apply(batch with { Sequence=3,OperationId=Guid.NewGuid().ToString("N") }).Status);
        Assert.Equal(notified,second.Receiver.ReadNotification(new(second.Publisher.LibraryId)).Revision);
        var rejected=operation with { Value="rejected",OperationId=Guid.NewGuid().ToString("N") };
        Assert.Equal(200,second.Receiver.Apply(new(second.Publisher.PublisherId,second.Publisher.Epoch,2,Guid.NewGuid().ToString("N"),"metadata-edits",
            JsonSerializer.SerializeToElement(new SpaceMetadataEditsBatch([rejected]),DeviceAuthorization.Json),[])).Status);
        Assert.Equal("accepted",Assert.Single(first.Receiver.ReadMetadataEdits().Edits).Value);
        Assert.Equal("rejected",Assert.Single(second.Receiver.ReadMetadataEdits().Edits).Value);
        Assert.Empty(unrelated.Receiver.ReadMetadataEdits().Edits);
    }

    [Fact]
    public void NewIdentityNeverReusesADeletedServerIntegerId()
    {
        var source=Publisher(Guid.NewGuid().ToString("N"));
        var first=Images(source.Publisher);
        Assert.Equal(200,source.Receiver.Apply(first).Status);
        var original=Assert.Single(_database.GetImages(includeMissing:true)).Id;
        using (var remove=_database.Command("DELETE FROM sync_entity_identities WHERE publisher_id=$publisher; DELETE FROM images WHERE id=$id"))
        {
            remove.Parameters.AddWithValue("$publisher",source.Publisher.PublisherId); remove.Parameters.AddWithValue("$id",original); remove.ExecuteNonQuery();
        }
        Assert.Equal(200,source.Receiver.Apply(Images(source.Publisher,2,@"C:\Photos\new.jpg")).Status);
        Assert.True(Assert.Single(_database.GetImages(includeMissing:true)).Id>original);
    }

    [Fact]
    public void ArchivedOperationProbeIsBoundToItsPublisherAndEpoch()
    {
        var source = Publisher(Guid.NewGuid().ToString("N"));
        var other = Publisher(Guid.NewGuid().ToString("N"));
        var video = new VideoRecord(1, 1, @"C:\Photos\clip.mp4", 10, DateTime.UnixEpoch, null, 0, 0, null, null, "active") { Sha256 = new string('f', 64) };
        Assert.Equal(200, source.Receiver.Apply(new(source.Publisher.PublisherId, source.Publisher.Epoch, 1, Guid.NewGuid().ToString("N"), "videos",
            JsonSerializer.SerializeToElement(new SpaceVideoBatch(1, @"C:\Photos", [video]), DeviceAuthorization.Json), [new("video", 1, Guid.NewGuid().ToString("N"))])).Status);
        var operation = Guid.NewGuid().ToString("N");
        var remoteOperation = DeviceAuthorization.Digest(source.Publisher.PublisherId + ":" + operation)[..32];
        using (var insert = _database.Command("INSERT INTO video_metadata_ownership_reviews(operation_id,content_id,kind,field,payload) SELECT $op,content_id,'video-concept','[]','{}' FROM videos LIMIT 1"))
        { insert.Parameters.AddWithValue("$op", remoteOperation); insert.ExecuteNonQuery(); }
        var request = new SpaceArchivedOperationRequest(source.Publisher.PublisherId, source.Publisher.Epoch, [operation]);
        Assert.Equal(operation, Assert.Single(source.Receiver.ReadArchivedOperations(request).OperationIds));
        Assert.Equal(remoteOperation, Assert.Single(source.Receiver.ReadArchivedOperations(request with { OperationIds = [remoteOperation] }).OperationIds));
        Assert.Empty(other.Receiver.ReadArchivedOperations(new(other.Publisher.PublisherId, other.Publisher.Epoch, [remoteOperation])).OperationIds);
        Assert.Empty(other.Receiver.ReadArchivedOperations(new(other.Publisher.PublisherId, other.Publisher.Epoch, [operation])).OperationIds);
        Assert.Throws<UnauthorizedAccessException>(() => other.Receiver.ReadArchivedOperations(request));
        Assert.Throws<InvalidOperationException>(() => source.Receiver.ReadArchivedOperations(request with { Epoch = Guid.NewGuid().ToString("N") }));
    }

    [Fact]
    public void AcceptedSourceMetadataInvalidatesTheGatewayCutWithoutNotifyingTheSameSourceAgain()
    {
        var source=Publisher(Guid.NewGuid().ToString("N")); var images=Images(source.Publisher);
        Assert.Equal(200,source.Receiver.Apply(images).Status);
        using var revision=_database.Command("SELECT metadata_revision FROM mirror_revision WHERE id=1");
        var before=Convert.ToInt64(revision.ExecuteScalar());
        var sourceBefore=source.Receiver.ReadNotification(new(source.Publisher.LibraryId));
        var readerBefore=source.Receiver.ReadNotification(new(null));
        var metadata=new SpaceMetadataBatch(new GatewayMetadata([],[],[]) with
        { CloudVisionAnalyses=[new(1,"test","model","prompt","completed","summary","{}",DateTime.UtcNow.ToString("O"))] });
        var receipt=source.Receiver.Apply(new(source.Publisher.PublisherId,source.Publisher.Epoch,2,Guid.NewGuid().ToString("N"),"metadata",
            JsonSerializer.SerializeToElement(metadata,DeviceAuthorization.Json),images.Identities));
        Assert.Equal(200,receipt.Status); Assert.True(Convert.ToInt64(revision.ExecuteScalar())>before);
        Assert.Equal(sourceBefore.Revision,source.Receiver.ReadNotification(new(source.Publisher.LibraryId)).Revision);
        Assert.True(source.Receiver.ReadNotification(new(null)).Revision>readerBefore.Revision);
        Assert.Equal("summary",Assert.Single(_database.GetAllMirroredCloudVisionAnalyses()).Summary);
    }

    [Fact]
    public void UnboundLegacyMediaCannotBeMisreportedAsACompletedSpaceInspection()
    {
        var source=_database.AddSource(@"C:\OldPhotos");
        _database.UpsertMirroredImage(new ImageRecord { Id=1,SourceId=source.Id,Path=@"C:\OldPhotos\one.jpg",
            FileSize=10,ModifiedUtc=DateTime.UtcNow,IndexedUtc=DateTime.UtcNow });
        var receiver=new SpaceSyncReceiver(_database,Device());
        var job=receiver.CreateInspection(new(Guid.NewGuid().ToString("N")));
        Assert.Equal("partially_failed",job.State);
        var target=Assert.Single(job.Targets); Assert.Equal("unbound",target.State);
        Assert.Equal("source_not_bound",target.ErrorCode);
        Assert.Equal("服务端来源：OldPhotos",target.DeviceName);
    }

    [Fact]
    public void CompletedSourcesStillRequireCentralOwnershipReview()
    {
        var source = Publisher(Guid.NewGuid().ToString("N"));
        Assert.Equal(200, source.Receiver.Apply(Images(source.Publisher)).Status);
        var sourceId = Assert.Single(_database.GetImages()).SourceId;
        new VideoRepository(_database).UpsertMirrored(new VideoRecord(10, sourceId,
            PublishedMediaPath.Qualify(source.Publisher.PublisherId, @"C:\Photos\clip.mp4"),
            10, DateTime.UnixEpoch, null, 0, 0, null, null, "active") { Sha256 = new string('f', 64) }, sourceId);
        var operation = Guid.NewGuid().ToString("N");
        var edit = new MetadataEdit("video-concept", 10, "[\"animal\",\"manual\"]", "accepted", DateTime.UtcNow.ToString("O"), operation)
            { VideoContentIdentity = "sha256:" + new string('f', 64) };
        using (var insert = _database.Command("INSERT INTO video_metadata_ownership_reviews(operation_id,content_id,kind,field,payload) SELECT $op,content_id,$kind,$field,$payload FROM videos WHERE id=10"))
        {
            insert.Parameters.AddWithValue("$op", operation); insert.Parameters.AddWithValue("$kind", edit.Kind);
            insert.Parameters.AddWithValue("$field", edit.Field); insert.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(edit));
            insert.ExecuteNonQuery();
        }
        var job = source.Receiver.CreateInspection(new(Guid.NewGuid().ToString("N")));
        Assert.Equal(1, job.PendingOwnershipReviews);
        var notice = source.Receiver.ReadNotification(new(source.Publisher.LibraryId));
        Assert.True(source.Receiver.ReportInspection(new(Assert.Single(notice.InspectionTargets), source.Publisher.PublisherId, notice.Epoch, "completed")));
        var pending = source.Receiver.ReadInspection(new(job.JobId));
        Assert.Equal("pending_review", pending.State);
        Assert.All(pending.Targets, target => Assert.Equal("completed", target.State));
        var review = Assert.Single(_database.GetVideoOwnershipReviews());
        _database.ResolveVideoOwnershipReview(new(operation, [], Discard: true, Epoch: review.Epoch), authoritative: true);
        var completed = source.Receiver.ReadInspection(new(job.JobId));
        Assert.Equal("completed", completed.State);
        Assert.Equal(0, completed.PendingOwnershipReviews);
    }

    [Fact]
    public void SpaceInspectionPersistsTargetsWaitsForOfflineSourcesAndCannotAcceptAnotherPublishersReceipt()
    {
        var first=Publisher(Guid.NewGuid().ToString("N")); var second=Publisher(Guid.NewGuid().ToString("N"));
        Assert.Equal(200,first.Receiver.Apply(Images(first.Publisher)).Status);
        Assert.Equal(200,second.Receiver.Apply(Images(second.Publisher)).Status);
        var request=new SpaceInspectionRequest(Guid.NewGuid().ToString("N"));
        var job=first.Receiver.CreateInspection(request);
        Assert.Equal(request.OperationId,job.JobId);
        Assert.Equal(2,job.Targets.Count); Assert.Equal("active",job.State);
        Assert.All(job.Targets,target=>Assert.Equal("offline",target.State));
        Assert.Equal(job.JobId,first.Receiver.CreateInspection(request).JobId);
        using (var connection=_database.OpenIndependentConnection())
            Assert.Equal(2,new SpaceSyncReceiver(connection,first.Device).ReadInspection(new(job.JobId)).Targets.Count);
        var firstNotice=first.Receiver.ReadNotification(new(first.Publisher.LibraryId));
        var firstTarget=Assert.Single(firstNotice.InspectionTargets);
        var secondNotice=second.Receiver.ReadNotification(new(second.Publisher.LibraryId));
        Assert.NotEqual(firstTarget,Assert.Single(secondNotice.InspectionTargets));
        Assert.Throws<UnauthorizedAccessException>(()=>second.Receiver.ReportInspection(new(firstTarget,first.Publisher.PublisherId,firstNotice.Epoch,"completed")));
        Assert.True(first.Receiver.ReportInspection(new(firstTarget,first.Publisher.PublisherId,firstNotice.Epoch,"running")));
        Assert.True(first.Receiver.ReportInspection(new(firstTarget,first.Publisher.PublisherId,firstNotice.Epoch,"completed")));
        Assert.True(first.Receiver.ReportInspection(new(firstTarget,first.Publisher.PublisherId,firstNotice.Epoch,"completed")));
        var waiting=first.Receiver.ReadInspection(new(job.JobId));
        Assert.Equal("active",waiting.State); Assert.Single(waiting.Targets.Where(target=>target.State=="completed"));
        var revoked=first.Receiver.ReadInspection(new(job.JobId),grant=>grant!=second.Device.GrantId);
        Assert.Equal("partially_failed",revoked.State);
        Assert.Equal("revoked",Assert.Single(revoked.Targets.Where(target=>target.PublisherId==second.Publisher.PublisherId)).State);
        Assert.False(first.Receiver.ReportInspection(new(firstTarget,first.Publisher.PublisherId,firstNotice.Epoch,"running")));
        var next=first.Receiver.CreateInspection(new(Guid.NewGuid().ToString("N")));
        var nextNotice=first.Receiver.ReadNotification(new(first.Publisher.LibraryId));
        Assert.Single(nextNotice.InspectionTargets);
        var cancelled=first.Receiver.CancelInspection(new(next.JobId));
        Assert.Equal("cancelled",cancelled.State); Assert.All(cancelled.Targets,target=>Assert.Equal("cancelled",target.State));
        Assert.False(first.Receiver.ReportInspection(new(nextNotice.InspectionTargets[0],first.Publisher.PublisherId,nextNotice.Epoch,"completed")));
        Assert.Empty(first.Receiver.ReadNotification(new(first.Publisher.LibraryId)).InspectionTargets);
    }

    [Fact]
    public void SourceNotificationsAreScopedAndADeletedPublisherCannotDisappearFromAnInspection()
    {
        var first=Publisher(Guid.NewGuid().ToString("N")); var second=Publisher(Guid.NewGuid().ToString("N"));
        Assert.Equal(200,first.Receiver.Apply(Images(first.Publisher)).Status);
        Assert.Equal(200,second.Receiver.Apply(Images(second.Publisher)).Status);
        var firstBefore=first.Receiver.ReadNotification(new(first.Publisher.LibraryId));
        var secondBefore=second.Receiver.ReadNotification(new(second.Publisher.LibraryId));
        using (var resolve=_database.Command("SELECT target_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind='image'"))
        {
            resolve.Parameters.AddWithValue("$publisher",first.Publisher.PublisherId);
            new Picer.Core.Vision.VisualConceptRepository(_database).Reject(Convert.ToInt64(resolve.ExecuteScalar()),"scene.mountain");
        }
        Assert.True(first.Receiver.ReadNotification(new(first.Publisher.LibraryId)).Revision>firstBefore.Revision);
        Assert.Equal(secondBefore.Revision,second.Receiver.ReadNotification(new(second.Publisher.LibraryId)).Revision);
        Assert.Empty(first.Receiver.ReadNotification(new(null)).InspectionTargets);
        Assert.Throws<UnauthorizedAccessException>(()=>new SpaceSyncReceiver(_database,first.Device with { Kind="agent" }).ReadNotification(new(first.Publisher.LibraryId)));
        var job=first.Receiver.CreateInspection(new(Guid.NewGuid().ToString("N")));
        using (var remove=_database.Command("DELETE FROM sync_publishers WHERE publisher_id=$id"))
        { remove.Parameters.AddWithValue("$id",second.Publisher.PublisherId); remove.ExecuteNonQuery(); }
        var remaining=first.Receiver.ReadInspection(new(job.JobId));
        Assert.Equal(2,remaining.Targets.Count);
        Assert.Equal("active",remaining.State);
        Assert.Equal("revoked",Assert.Single(remaining.Targets.Where(target=>target.PublisherId==second.Publisher.PublisherId)).State);
    }

    [Fact]
    public void NotificationWaitHasAnIndependentAdmissionBudget()
    {
        var budget=new SpaceRequestAdmission(); var device=Device();
        using var notice=budget.TryEnter(device,"/api/sync/v2/notifications"); Assert.NotNull(notice);
        using var write=budget.TryEnter(device,"/api/sync/v2/batches"); Assert.NotNull(write);
        Assert.Null(budget.TryEnter(device,"/api/sync/v2/notifications"));
        Assert.Null(budget.TryEnter(device,"/api/sync/v2/batches"));
    }

    [Fact]
    public void ContentHashRejectionChangeAndRestorationAreDeliveredOnlyToTheOwningPublisher()
    {
        var first = Publisher(Guid.NewGuid().ToString("N"));
        var second = Publisher(Guid.NewGuid().ToString("N"));
        var a = Images(first.Publisher); var b = Images(second.Publisher);
        Assert.Equal(200, first.Receiver.Apply(a).Status);
        Assert.Equal(200, second.Receiver.Apply(b).Status);
        using var resolve = _database.Command("SELECT target_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind='image'");
        resolve.Parameters.AddWithValue("$publisher", first.Publisher.PublisherId);
        var image = Convert.ToInt64(resolve.ExecuteScalar());
        new Picer.Core.Vision.VisualConceptRepository(_database).Reject(image, "scene.mountain");
        var page = first.Receiver.ReadMetadataChanges(0, 100);
        Assert.Equal("scene.mountain", Assert.Single(Assert.Single(page.Changes).Rejections).ConceptId);
        Assert.Empty(second.Receiver.ReadMetadataChanges(0, 100).Changes);
        new Picer.Core.Vision.ClassificationProjectionService(_database).RestoreSuggestion(image, "scene.mountain");
        Assert.Empty(Assert.Single(first.Receiver.ReadMetadataChanges(page.Through, 100).Changes).Rejections);
        Assert.Empty(second.Receiver.ReadMetadataChanges(0, 100).Changes);
    }

    [Fact]
    public void DifferentMachinesWithSameLocalIdsAndPathsKeepBothReferences()
    {
        var library = Guid.NewGuid().ToString("N");
        var first = Publisher(library);
        var second = Publisher(library); // A cloned library on another device gets its own incarnation.
        Assert.NotEqual(first.Publisher.PublisherId, second.Publisher.PublisherId);
        Assert.Equal(200, first.Receiver.Apply(Images(first.Publisher)).Status);
        Assert.Equal(200, second.Receiver.Apply(Images(second.Publisher)).Status);
        var sources = _database.GetSources();
        Assert.Equal(2, sources.Count);
        var images = sources.SelectMany(source => _database.GetImages(source.Id)).ToArray();
        Assert.Equal(2, images.Length);
        Assert.NotEqual(images[0].Id, images[1].Id);
        Assert.NotEqual(images[0].Path, images[1].Path);
        Assert.All(images, image =>
        {
            Assert.True(PublishedMediaPath.TryUnqualify(image.Path, out _, out var original));
            Assert.Equal(@"C:\Photos\same.jpg", original);
        });
        using var contents = _database.Command("SELECT COUNT(*) FROM image_contents");
        Assert.Equal(1L, contents.ExecuteScalar());
    }

    [Fact]
    public void RetriedOperationReturnsOriginalReceiptAndChangedReplayCannotOverwrite()
    {
        var publisher = Publisher(Guid.NewGuid().ToString("N"));
        var batch = Images(publisher.Publisher);
        var first = publisher.Receiver.Apply(batch);
        Assert.Equal(first, publisher.Receiver.Apply(batch));
        Assert.Throws<InvalidOperationException>(() => publisher.Receiver.Apply(batch with { OperationId = Guid.NewGuid().ToString("N") }));
        Assert.Single(_database.GetImages(Assert.Single(_database.GetSources()).Id));
    }

    [Fact]
    public void InvalidPayloadRollsBackMappingsAndBusinessRowsButConsumesSequence()
    {
        var publisher = Publisher(Guid.NewGuid().ToString("N"));
        var bad = Images(publisher.Publisher, path: @"C:\Other\secret.jpg");
        Assert.Equal(422, publisher.Receiver.Apply(bad).Status);
        Assert.Empty(_database.GetSources());
        using var identities = _database.Command("SELECT COUNT(*) FROM sync_entity_identities");
        Assert.Equal(0L, identities.ExecuteScalar());
        var good = Images(publisher.Publisher, 2);
        Assert.Equal(200, publisher.Receiver.Apply(good).Status);
        Assert.Single(_database.GetSources());
    }

    [Fact]
    public void PhotoAndVideoPublishersCannotOverwriteEachOthersNumericIds()
    {
        var first = Publisher(Guid.NewGuid().ToString("N"));
        var second = Publisher(Guid.NewGuid().ToString("N"));
        foreach (var entry in new[] { first, second })
        {
            var payload = new SpaceVideoBatch(1, @"C:\Photos", [new VideoRecord(1, 1, @"C:\Photos\same.mp4", 200,
                DateTime.UtcNow, 30, 1920, 1080, null, null, "active") { Sha256 = new string('b', 64) }]);
            var batch = new SpaceSyncBatch(entry.Publisher.PublisherId, entry.Publisher.Epoch, 1, Guid.NewGuid().ToString("N"), "videos",
                JsonSerializer.SerializeToElement(payload, DeviceAuthorization.Json), [new("video", 1, Guid.NewGuid().ToString("N"))]);
            Assert.Equal(200, entry.Receiver.Apply(batch).Status);
        }
        var videos = new VideoRepository(_database).GetAll();
        Assert.Equal(2, videos.Count);
        Assert.Equal(2, videos.Select(video => video.Id).Distinct().Count());
        using var content = _database.Command("SELECT COUNT(*) FROM video_contents");
        Assert.Equal(1L, content.ExecuteScalar());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedVideoContentDoesNotOfferAnotherPublishersPersonIdentity(bool sanitizedFramePath)
    {
        var first=Publisher(Guid.NewGuid().ToString("N"));var second=Publisher(Guid.NewGuid().ToString("N"));
        foreach(var entry in new[]{first,second})
        {
            var video=new VideoRecord(1,1,@"C:\Photos\shared.mp4",200,DateTime.UtcNow,30,1920,1080,null,null,"active") { Sha256=new string('d',64) };
            Assert.Equal(200,entry.Receiver.Apply(new(entry.Publisher.PublisherId,entry.Publisher.Epoch,1,Guid.NewGuid().ToString("N"),"videos",
                JsonSerializer.SerializeToElement(new SpaceVideoBatch(1,@"C:\Photos",[video]),DeviceAuthorization.Json),[new("video",1,Guid.NewGuid().ToString("N"))])).Status);
        }
        var catalog=new SpaceCatalogBatch([],[],[new PersonSummary(1,"来源一的测试人物",0,0)]);
        Assert.Equal(200,first.Receiver.Apply(new(first.Publisher.PublisherId,first.Publisher.Epoch,2,Guid.NewGuid().ToString("N"),"catalog",
            JsonSerializer.SerializeToElement(catalog,DeviceAuthorization.Json),[new("person",1,Guid.NewGuid().ToString("N"))])).Status);
        var now=DateTime.UtcNow.ToString("O");
        var metadata=new GatewayMetadata([],[],[]) with {
            VideoKeyframes=[new(1,1,0,sanitizedFramePath?"":@"C:\Cache\frame.jpg",123)],
            VideoFaces=[new(1,1,1,0,0,0,0,10,10,0.9,null,null,null,1,"confirmed",now,now) { ModelVersion="test-model" }] };
        var receipt=first.Receiver.Apply(new(first.Publisher.PublisherId,first.Publisher.Epoch,3,Guid.NewGuid().ToString("N"),"metadata",
            JsonSerializer.SerializeToElement(new SpaceMetadataBatch(metadata),DeviceAuthorization.Json),
            [new("keyframe",1,Guid.NewGuid().ToString("N")),new("video-face",1,Guid.NewGuid().ToString("N"))]));
        Assert.True(receipt.Status==200,receipt.Code);
        var scope=new SpaceUploadScope([1],Photos:false,People:true);
        Assert.DoesNotContain(second.Receiver.ReadIdentityOffers(new(second.Publisher.PublisherId,second.Publisher.Epoch,scope)).Offers,
            offer=>offer.Kind=="person");
        var state=new MetadataEdit("video-face-state",1,"state",JsonSerializer.Serialize(new FaceManualState(null,"ignored","test-model")),now,Guid.NewGuid().ToString("N"));
        var rejection=new MetadataEdit("video-face-rejection",1,"1",JsonSerializer.Serialize(new FacePersonRejection(true,"test-model")),now,Guid.NewGuid().ToString("N"));
        var edited=first.Receiver.Apply(new(first.Publisher.PublisherId,first.Publisher.Epoch,4,Guid.NewGuid().ToString("N"),"metadata-edits",
            JsonSerializer.SerializeToElement(new SpaceMetadataEditsBatch([state,rejection]),DeviceAuthorization.Json),[]));
        Assert.True(edited.Status==200,edited.Code);
        var fields=first.Receiver.ReadMetadataEditPage(0,"",null,500,first.Publisher.Epoch).Edits;
        Assert.Equal("ignored",PicerDatabase.ParseFaceManualState(fields.Single(edit=>edit.Kind=="video-face-state").Value).State);
        var rejected=fields.Single(edit=>edit.Kind=="video-face-rejection");Assert.Equal("1",rejected.Field);Assert.Equal(1,rejected.EntityId);
        Assert.DoesNotContain(second.Receiver.ReadMetadataEditPage(0,"",null,500,second.Publisher.Epoch).Edits,
            edit=>edit.Kind.StartsWith("video-face-",StringComparison.Ordinal));
        var withdrawal=rejected with { Value=JsonSerializer.Serialize(new FacePersonRejection(false,"test-model")),
            BaseVersion=rejected.ServerVersion,ServerVersion=0,OperationId=Guid.NewGuid().ToString("N") };
        var cleared=first.Receiver.Apply(new(first.Publisher.PublisherId,first.Publisher.Epoch,5,Guid.NewGuid().ToString("N"),"metadata-edits",
            JsonSerializer.SerializeToElement(new SpaceMetadataEditsBatch([withdrawal]),DeviceAuthorization.Json),[]));
        Assert.True(cleared.Status==200,cleared.Code);
        Assert.False(PicerDatabase.ParseFacePersonRejection(first.Receiver.ReadMetadataEdits().Edits.Single(edit=>edit.Kind=="video-face-rejection").Value).Rejected);
    }

    [Fact]
    public void CatalogAndFaceRelationsAreRemappedAndSharedTagConverges()
    {
        var first = Publisher(Guid.NewGuid().ToString("N"));
        var second = Publisher(Guid.NewGuid().ToString("N"));
        foreach (var entry in new[] { first, second })
        {
            var image = Images(entry.Publisher);
            Assert.Equal(200, entry.Receiver.Apply(image).Status);
            var catalog = new SpaceCatalogBatch([new Tag { Id = 1, Name = "旅行", CreatedUtc = DateTime.UtcNow }],
                [new Album { Id = 1, Name = "相册", CreatedUtc = DateTime.UtcNow }], [new PersonSummary(1, "家人", 0, 0)]);
            var catalogBatch = new SpaceSyncBatch(entry.Publisher.PublisherId, entry.Publisher.Epoch, 2, Guid.NewGuid().ToString("N"), "catalog",
                JsonSerializer.SerializeToElement(catalog, DeviceAuthorization.Json), [new("tag", 1, Guid.NewGuid().ToString("N")),
                    new("album", 1, Guid.NewGuid().ToString("N")), new("person", 1, Guid.NewGuid().ToString("N"))]);
            Assert.Equal(200, entry.Receiver.Apply(catalogBatch).Status);
            var now = DateTime.UtcNow.ToString("O");
            var metadata = new GatewayMetadata([new(1, 1)], [new(1, 1)], [],
                [new(1, 1, "model-v1", 0, 0, 0, 10, 10, 0.99, null, null, now)],
                [new(1, 1, "confirmed", 0.99, "user", now)], [], [], []);
            var metaBatch = new SpaceSyncBatch(entry.Publisher.PublisherId, entry.Publisher.Epoch, 3, Guid.NewGuid().ToString("N"), "metadata",
                JsonSerializer.SerializeToElement(new SpaceMetadataBatch(metadata), DeviceAuthorization.Json), [new("face", 1, Guid.NewGuid().ToString("N"))]);
            Assert.Equal(200, entry.Receiver.Apply(metaBatch).Status);
            var manual=new MetadataEdit("face-state",1,"state",JsonSerializer.Serialize(new FaceManualState(1,"confirmed","model-v1")),
                DateTime.UtcNow.ToString("O"),Guid.NewGuid().ToString("N"));
            Assert.Equal(200,entry.Receiver.Apply(new(entry.Publisher.PublisherId,entry.Publisher.Epoch,4,Guid.NewGuid().ToString("N"),"metadata-edits",
                JsonSerializer.SerializeToElement(new SpaceMetadataEditsBatch([manual]),DeviceAuthorization.Json),[])).Status);
            var roundTrip=Assert.Single(entry.Receiver.ReadMetadataEdits().Edits.Where(edit=>edit.Kind=="face-state"));
            Assert.Equal(1,roundTrip.EntityId);Assert.Equal(1,PicerDatabase.ParseFaceManualState(roundTrip.Value).PersonId);
        }
        Assert.Single(SemanticEngine.GetTags(_database));
        Assert.Equal(2, SemanticEngine.GetAlbums(_database).Count);
        Assert.Equal(2, new PeopleService(_database).GetAll().Count);
        Assert.Equal(2, _database.GetAllMirroredFaceAssignments().Count);
        Assert.All(_database.GetSources().SelectMany(source => _database.GetImages(source.Id)), image =>
            Assert.Single(SemanticEngine.GetImageTags(_database, image.Id)));
        long firstFace;
        using(var mapping=_database.Command("SELECT target_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind='face' AND local_key='1'"))
        { mapping.Parameters.AddWithValue("$publisher",first.Publisher.PublisherId);firstFace=Convert.ToInt64(mapping.ExecuteScalar()); }
        new PeopleService(_database).MoveFaceToUnassigned(firstFace);
        var firstState=Assert.Single(first.Receiver.ReadMetadataEditPage(0,"",null,500,first.Publisher.Epoch).Edits.Where(edit=>edit.Kind=="face-state"));
        var secondState=Assert.Single(second.Receiver.ReadMetadataEditPage(0,"",null,500,second.Publisher.Epoch).Edits.Where(edit=>edit.Kind=="face-state"));
        Assert.Equal("unassigned",PicerDatabase.ParseFaceManualState(firstState.Value).State);
        Assert.Equal("confirmed",PicerDatabase.ParseFaceManualState(secondState.Value).State);
        long firstPerson;
        using(var mapping=_database.Command("SELECT target_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind='person' AND local_key='1'"))
        { mapping.Parameters.AddWithValue("$publisher",first.Publisher.PublisherId);firstPerson=Convert.ToInt64(mapping.ExecuteScalar()); }
        new PeopleService(_database).MarkNotThisPerson(firstFace,firstPerson);
        var rejection=Assert.Single(first.Receiver.ReadMetadataEditPage(0,"",null,500,first.Publisher.Epoch).Edits.Where(edit=>edit.Kind=="face-rejection"));
        Assert.Equal("1",rejection.Field);Assert.Equal(1,rejection.EntityId);
        Assert.True(PicerDatabase.ParseFacePersonRejection(rejection.Value).Rejected);
        Assert.DoesNotContain(second.Receiver.ReadMetadataEditPage(0,"",null,500,second.Publisher.Epoch).Edits,edit=>edit.Kind=="face-rejection");
    }

    [Fact]
    public void ClassificationMetadataIsMappedAndKeptInsideItsPublisherSource()
    {
        var first = Publisher(Guid.NewGuid().ToString("N"));
        var second = Publisher(Guid.NewGuid().ToString("N"));
        foreach (var entry in new[] { first, second })
            Assert.Equal(200, entry.Receiver.Apply(Images(entry.Publisher)).Status);

        var firstImage = _database.GetImages(_database.GetSources().Single(source => source.Path.Contains(first.Publisher.PublisherId)).Id).Single();
        var secondImage = _database.GetImages(_database.GetSources().Single(source => source.Path.Contains(second.Publisher.PublisherId)).Id).Single();
        var now = DateTime.UtcNow.ToString("O");
        foreach (var entry in new[] { first, second })
        {
            var concept = entry.Publisher.PublisherId == first.Publisher.PublisherId ? "scene.first" : "scene.second";
            var metadata = new GatewayMetadata([], [], []) with
            {
                ClassificationAssertions = [new GatewayClassificationAssertion(1, new string('a', 64), concept,
                    "user", "user-v1", "user_confirmed", "accepted", null, "manual", now, now, now)],
                ClassificationRejections = [new GatewayClassificationRejection(1, "scene.rejected", "vision_model", now)],
                SemanticContentClusters = [new GatewaySemanticContentCluster("same-local-cluster", "clip-v1", concept, 0.9f, 1, now, now)],
                SemanticContentClusterMembers = [new GatewaySemanticContentClusterMember("same-local-cluster", 1, 0.9f)]
            };
            var batch = new SpaceSyncBatch(entry.Publisher.PublisherId, entry.Publisher.Epoch, 2,
                Guid.NewGuid().ToString("N"), "metadata", JsonSerializer.SerializeToElement(new SpaceMetadataBatch(metadata), DeviceAuthorization.Json), []);
            Assert.Equal(200, entry.Receiver.Apply(batch).Status);
        }

        using var assertions = _database.Command("SELECT image_id,concept_id FROM image_classification_assertions ORDER BY image_id");
        using var rows = assertions.ExecuteReader();
        Assert.True(rows.Read()); Assert.Equal(firstImage.Id, rows.GetInt64(0)); Assert.Equal("scene.first", rows.GetString(1));
        Assert.True(rows.Read()); Assert.Equal(secondImage.Id, rows.GetInt64(0)); Assert.Equal("scene.second", rows.GetString(1));
        Assert.False(rows.Read());
        using var rejections = _database.Command("SELECT image_id,COUNT(*) FROM image_classification_rejections GROUP BY image_id ORDER BY image_id");
        using var rejected = rejections.ExecuteReader();
        Assert.True(rejected.Read()); Assert.Equal(firstImage.Id, rejected.GetInt64(0)); Assert.Equal(1, rejected.GetInt32(1));
        Assert.True(rejected.Read()); Assert.Equal(secondImage.Id, rejected.GetInt64(0)); Assert.Equal(1, rejected.GetInt32(1));
        Assert.False(rejected.Read());
        using var clusters = _database.Command("SELECT COUNT(DISTINCT cluster_key) FROM semantic_content_clusters");
        Assert.Equal(2L, clusters.ExecuteScalar());
        using var members = _database.Command("SELECT COUNT(*) FROM semantic_content_cluster_members");
        Assert.Equal(2L, members.ExecuteScalar());
    }

    [Fact]
    public void ClassificationChangeFeedIsRestrictedToOwningPublisher()
    {
        var first = Publisher(Guid.NewGuid().ToString("N"));
        var second = Publisher(Guid.NewGuid().ToString("N"));
        Assert.Equal(200, first.Receiver.Apply(Images(first.Publisher)).Status);
        Assert.Equal(200, second.Receiver.Apply(Images(second.Publisher)).Status);
        Assert.Empty(first.Receiver.ReadMetadataChanges(0, 100).Changes); // v2 upload is not echoed to its origin.
        long targetImage;
        using (var find = _database.Command("SELECT target_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind='image' AND local_key='1'"))
        {
            find.Parameters.AddWithValue("$publisher", first.Publisher.PublisherId);
            targetImage = Convert.ToInt64(find.ExecuteScalar());
        }
        var now = DateTime.UtcNow.ToString("O");
        using (var change = _database.Command("""
            INSERT INTO image_classification_assertions(image_id,content_sha256,concept_id,source_kind,source_version,assertion_state,review_state,
                confidence,evidence_fingerprint,created_utc,updated_utc)
            VALUES($image,$sha,'scene.remote','user','1','user_confirmed','accepted',1.0,'remote-review',$now,$now)
            """))
        {
            change.Parameters.AddWithValue("$image", targetImage); change.Parameters.AddWithValue("$sha", new string('a', 64));
            change.Parameters.AddWithValue("$now", now); change.ExecuteNonQuery();
        }
        using (var description = _database.Command("""
            INSERT INTO cloud_vision_analyses(image_id,provider,model_version,prompt_version,source_file_size,source_modified_utc,location_fingerprint,
                status,summary,result_json,updated_utc)
            VALUES($image,'vlm','model-v1','prompt-v1',100,$now,'test','completed','远程描述','{}',$now)
            """))
        { description.Parameters.AddWithValue("$image", targetImage); description.Parameters.AddWithValue("$now", now); description.ExecuteNonQuery(); }
        var faceId = targetImage + 10_000;
        var faceGlobalId = Guid.NewGuid().ToString("N");
        using (var identity = _database.Command("INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id,target_id) VALUES($publisher,'face','1',$global,$target)"))
        {
            identity.Parameters.AddWithValue("$publisher", first.Publisher.PublisherId); identity.Parameters.AddWithValue("$global", faceGlobalId);
            identity.Parameters.AddWithValue("$target", faceId); identity.ExecuteNonQuery();
        }
        using (var face = _database.Command("""
            INSERT INTO detected_faces(id,image_id,model_version,ordinal,box_x,box_y,box_width,box_height,confidence,created_utc)
            VALUES($id,$image,'face-v1',0,0,0,10,10,0.99,$now)
            """))
        { face.Parameters.AddWithValue("$id", faceId); face.Parameters.AddWithValue("$image", targetImage); face.Parameters.AddWithValue("$now", now); face.ExecuteNonQuery(); }
        using (var review = _database.Command("INSERT INTO face_corrections(face_id,action,created_utc) VALUES($face,'sync_review_ignored',$now)"))
        { review.Parameters.AddWithValue("$face", faceId); review.Parameters.AddWithValue("$now", now); review.ExecuteNonQuery(); }

        var firstPage = first.Receiver.ReadMetadataChanges(0, 100);
        var secondPage = second.Receiver.ReadMetadataChanges(0, 100);
        Assert.Equal(first.Publisher.PublisherId, firstPage.PublisherId);
        Assert.Equal(first.Publisher.Epoch, firstPage.Epoch);
        var changeRow = Assert.Single(firstPage.Changes);
        Assert.Equal(first.Publisher.PublisherId, ReadPublisherForGlobalImage(changeRow.ImageGlobalId));
        Assert.Contains(changeRow.Assertions, assertion => assertion.ConceptId == "scene.remote" && assertion.ReviewState == "accepted");
        Assert.Contains(changeRow.PhotoAnalyses!, analysis => analysis.Summary == "远程描述");
        Assert.Contains(changeRow.Faces!, face => face.Id == 1 && face.ImageId == 1 && face.Embedding is null);
        Assert.Contains(changeRow.FaceReviewDecisions!, decision => decision.FaceId == 1 && decision.State == "ignored");
        Assert.Empty(secondPage.Changes);
        Assert.Equal(0, secondPage.Through);
    }

    [Fact]
    public void VideoDescriptionChangeIsReturnedOnlyToOwningPublisher()
    {
        var owner = Publisher(Guid.NewGuid().ToString("N"));
        var other = Publisher(Guid.NewGuid().ToString("N"));
        var video = new VideoRecord(1, 1, @"C:\Videos\clip.mp4", 200, DateTime.UtcNow, 12, 1280, 720, null, null, "active")
        { Sha256 = new string('b', 64) };
        var batch = new SpaceSyncBatch(owner.Publisher.PublisherId, owner.Publisher.Epoch, 1, Guid.NewGuid().ToString("N"), "videos",
            JsonSerializer.SerializeToElement(new SpaceVideoBatch(1, @"C:\Videos", [video]), DeviceAuthorization.Json),
            [new("video", 1, Guid.NewGuid().ToString("N"))]);
        Assert.Equal(200, owner.Receiver.Apply(batch).Status);
        long targetVideo;
        long contentId;
        using (var find = _database.Command("SELECT target_id FROM sync_entity_identities WHERE publisher_id=$publisher AND kind='video' AND local_key='1'"))
        { find.Parameters.AddWithValue("$publisher", owner.Publisher.PublisherId); targetVideo = Convert.ToInt64(find.ExecuteScalar()); }
        using (var find = _database.Command("SELECT content_id FROM videos WHERE id=$id"))
        { find.Parameters.AddWithValue("$id", targetVideo); contentId = Convert.ToInt64(find.ExecuteScalar()); }
        var now = DateTime.UtcNow.ToString("O");
        using (var summary = _database.Command("""
            INSERT INTO video_summaries(video_id,provider,model_version,prompt_version,source_file_size,source_modified_utc,summary,result_json,
                input_tokens,output_tokens,estimated_cost_yuan,created_utc)
            VALUES($content,'vlm','model-v1','prompt-v1',200,$now,'video description','{}',0,0,0,$now)
            """))
        { summary.Parameters.AddWithValue("$content", contentId); summary.Parameters.AddWithValue("$now", now); summary.ExecuteNonQuery(); }
        using (var identity = _database.Command("INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id,target_id) VALUES($publisher,'keyframe','1',$global,1000)"))
        {
            identity.Parameters.AddWithValue("$publisher", owner.Publisher.PublisherId);
            identity.Parameters.AddWithValue("$global", Guid.NewGuid().ToString("N")); identity.ExecuteNonQuery();
        }
        _database.UpsertMirroredVideoMetadata(new GatewayMetadata([], [], []) with
        {
            VideoKeyframes = [new(1000, targetVideo, 0, PublishedMediaPath.Qualify(Guid.NewGuid().ToString("N"), @"D:\OtherSource\private-frame.jpg"), 0)]
        });

        var ownerPage = owner.Receiver.ReadMetadataChanges(0, 100);
        var otherPage = other.Receiver.ReadMetadataChanges(0, 100);
        var change = Assert.Single(ownerPage.Changes);
        Assert.Equal("video", change.MediaKind);
        Assert.Contains(change.VideoSummaries!, item => item.Summary == "video description");
        Assert.Equal(now, Assert.Single(change.VideoSummaries!).UpdatedUtc);
        Assert.Equal(string.Empty, Assert.Single(change.VideoKeyframes!).Path);
        Assert.Empty(otherPage.Changes);
    }

    [Fact]
    public void MetadataEditsAreTranslatedBackToAndFilteredByOwningPublisher()
    {
        var owner = Publisher(Guid.NewGuid().ToString("N"));
        var other = Publisher(Guid.NewGuid().ToString("N"));
        foreach (var entry in new[] { owner, other })
        {
            Assert.Equal(200, entry.Receiver.Apply(Images(entry.Publisher)).Status);
            var catalog = new SpaceCatalogBatch([new Tag { Id = 1, Name = "tag-" + entry.Publisher.PublisherId, CreatedUtc = DateTime.UtcNow }], [], []);
            var catalogBatch = new SpaceSyncBatch(entry.Publisher.PublisherId, entry.Publisher.Epoch, 2, Guid.NewGuid().ToString("N"), "catalog",
                JsonSerializer.SerializeToElement(catalog, DeviceAuthorization.Json), [new("tag", 1, Guid.NewGuid().ToString("N"))]);
            Assert.Equal(200, entry.Receiver.Apply(catalogBatch).Status);
        }
        var now = DateTime.UtcNow.ToString("O");
        var edits = new SpaceMetadataEditsBatch([new("tag-link", 1, "1", "1", now, Guid.NewGuid().ToString("N"))]);
        var editBatch = new SpaceSyncBatch(owner.Publisher.PublisherId, owner.Publisher.Epoch, 3, Guid.NewGuid().ToString("N"), "metadata-edits",
            JsonSerializer.SerializeToElement(edits, DeviceAuthorization.Json), []);
        Assert.Equal(200, owner.Receiver.Apply(editBatch).Status);

        var returned = Assert.Single(owner.Receiver.ReadMetadataEdits().Edits);
        Assert.Equal(new MetadataEdit("tag-link", 1, "1", "1", now, returned.OperationId) { ServerVersion = returned.ServerVersion }, returned);
        Assert.True(returned.ServerVersion > 0);
        Assert.Empty(other.Receiver.ReadMetadataEdits().Edits);
    }

    [Fact]
    public void DownloadedClassificationAndDescriptionApplyAtomicallyToMappedLocalImage()
    {
        using var local = new PicerDatabase(Path.Combine(_root, "local-publisher.db"));
        var source = local.AddSource(@"C:\LocalPhotos");
        var now = DateTime.UtcNow;
        var image = new ImageRecord { Id = 7, SourceId = source.Id, Path = @"C:\LocalPhotos\one.jpg", FileSize = 100,
            ModifiedUtc = now, IndexedUtc = now, Status = ImageStatus.Active };
        local.UpsertMirroredImage(image);
        local.UpsertMirroredImage(new ImageRecord { Id = 8, SourceId = source.Id, Path = @"C:\LocalPhotos\unrelated.jpg", FileSize = 100,
            ModifiedUtc = now, IndexedUtc = now, Status = ImageStatus.Active });
        local.UpsertMirroredLocation(new(7, false, "test-country", null, null, null, null, null, null));
        local.UpsertMirroredLocation(new(8, false, "test-country", null, null, null, null, null, null));
        local.UpsertMirroredFace(new(11, 7, "face-v1", 0, 0, 0, 1, 1, 1, null, [1, 2], now.ToString("O")));
        local.UpsertMirroredFace(new(12, 8, "face-v1", 0, 0, 0, 1, 1, 1, null, [3, 4], now.ToString("O")));
        var publisherId = Guid.NewGuid().ToString("N");
        var globalId = Guid.NewGuid().ToString("N");
        var epoch = Guid.NewGuid().ToString("N");
        using (var publisher = local.Command("INSERT INTO sync_publishers(publisher_id,library_id,incarnation_id,device_grant_id,space_id,state,epoch) VALUES($id,$library,$device,$grant,$space,'publishing',$epoch)"))
        {
            publisher.Parameters.AddWithValue("$id", publisherId); publisher.Parameters.AddWithValue("$library", Guid.NewGuid().ToString("N"));
            publisher.Parameters.AddWithValue("$device", Guid.NewGuid().ToString("N")); publisher.Parameters.AddWithValue("$grant", Guid.NewGuid().ToString("N"));
            publisher.Parameters.AddWithValue("$space", Guid.NewGuid().ToString("N")); publisher.Parameters.AddWithValue("$epoch", epoch); publisher.ExecuteNonQuery();
        }
        using (var identity = local.Command("INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id) VALUES($publisher,'image','7',$global)"))
        { identity.Parameters.AddWithValue("$publisher", publisherId); identity.Parameters.AddWithValue("$global", globalId); identity.ExecuteNonQuery(); }
        foreach (var face in new[] { 11L, 12L })
        {
            using var identity = local.Command("INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id) VALUES($publisher,'face',$local,$global)");
            identity.Parameters.AddWithValue("$publisher", publisherId); identity.Parameters.AddWithValue("$local", face.ToString());
            identity.Parameters.AddWithValue("$global", Guid.NewGuid().ToString("N")); identity.ExecuteNonQuery();
        }
        var timestamp = now.ToString("O");
        using (var oldLocal = local.Command("""
            INSERT INTO cloud_vision_analyses(image_id,provider,model_version,prompt_version,source_file_size,source_modified_utc,location_fingerprint,status,summary,result_json,updated_utc)
            VALUES(7,'vlm','old-model','prompt-v1',100,$modified,'local','completed','older local value','{}',$modified)
            """))
        { oldLocal.Parameters.AddWithValue("$modified", now.AddDays(-1).ToString("O")); oldLocal.ExecuteNonQuery(); }
        long eventsBeforeApply;
        using (var events = local.Command("SELECT COUNT(*) FROM sync_metadata_changes")) eventsBeforeApply = Convert.ToInt64(events.ExecuteScalar());
        long revisionBeforeApply;
        using (var revision = local.Command("SELECT metadata_revision FROM mirror_revision WHERE id=1")) revisionBeforeApply = Convert.ToInt64(revision.ExecuteScalar());
        var change = new SpaceMetadataChange(9, globalId,
            [new GatewayClassificationAssertion(0, new string('a', 64), "scene.remote", "user", "1", "user_confirmed", "accepted", 1,
                "review", timestamp, timestamp, timestamp)], [], [], [], PhotoAnalyses:
            [new GatewayCloudVisionAnalysis(0, "vlm", "model-v1", "prompt-v1", "completed", "描述已同步", "{}", timestamp, 100, timestamp)]);
        var restrictedChange = change with { Faces = [], Locations = [] };
        var applied = SpaceSyncPublisher.ApplyDownloadedMetadataPage(local, publisherId, new(9, false, [restrictedChange]),
            new([source.Id], People: false, PreciseLocation: false));
        Assert.Equal(1, applied);
        using (var assertion = local.Command("SELECT review_state FROM image_classification_assertions WHERE image_id=7 AND concept_id='scene.remote'"))
            Assert.Equal("accepted", assertion.ExecuteScalar());
        using (var description = local.Command("SELECT summary FROM cloud_vision_analyses WHERE image_id=7 AND provider='vlm'"))
            Assert.Equal("描述已同步", description.ExecuteScalar());
        // A privacy-limited upload must not erase local-only people/location data.
        using (var faces = local.Command("SELECT COUNT(*) FROM detected_faces")) Assert.Equal(2L, faces.ExecuteScalar());
        using (var locations = local.Command("SELECT COUNT(*) FROM image_locations")) Assert.Equal(2L, locations.ExecuteScalar());
        using (var revision = local.Command("SELECT metadata_revision FROM mirror_revision WHERE id=1")) Assert.Equal(revisionBeforeApply, revision.ExecuteScalar());
        using (var events = local.Command("SELECT COUNT(*) FROM sync_metadata_changes"))
            Assert.Equal(eventsBeforeApply, Convert.ToInt64(events.ExecuteScalar()));
        using (var updateNewer = local.Command("UPDATE cloud_vision_analyses SET summary='较新的本地版本',updated_utc='2099-01-01T00:00:00Z' WHERE image_id=7 AND provider='vlm'")) updateNewer.ExecuteNonQuery();
        _ = SpaceSyncPublisher.ApplyDownloadedMetadataPage(local, publisherId, new(10, false, [change with { ChangeId = 10 }]));
        // Omitted fields are not an empty authoritative snapshot (older peers).
        using (var faces = local.Command("SELECT COUNT(*) FROM detected_faces")) Assert.Equal(2L, faces.ExecuteScalar());
        using (var locations = local.Command("SELECT COUNT(*) FROM image_locations")) Assert.Equal(2L, locations.ExecuteScalar());
        using (var preserved = local.Command("SELECT summary FROM cloud_vision_analyses WHERE image_id=7 AND provider='vlm'"))
            Assert.Equal("较新的本地版本", preserved.ExecuteScalar());
        using (var cursor = local.Command("SELECT downloaded_sequence FROM sync_publishers WHERE publisher_id=$publisher"))
        { cursor.Parameters.AddWithValue("$publisher", publisherId); Assert.Equal(10L, cursor.ExecuteScalar()); }
        using (var events = local.Command("SELECT COUNT(*) FROM sync_metadata_changes")) Assert.Equal(eventsBeforeApply, Convert.ToInt64(events.ExecuteScalar()));
        var removedClassification = change with { ChangeId = 11, Assertions = [], Rejections = [], Faces = [], PhotoAnalyses = [], Locations = [] };
        _ = SpaceSyncPublisher.ApplyDownloadedMetadataPage(local, publisherId, new(11, false, [removedClassification]));
        using (var deleted = local.Command("SELECT COUNT(*) FROM image_classification_assertions WHERE image_id=7"))
            Assert.Equal(0L, Convert.ToInt64(deleted.ExecuteScalar()));
        using (var deleted = local.Command("SELECT COUNT(*) FROM cloud_vision_analyses WHERE image_id=7")) Assert.Equal(0L, deleted.ExecuteScalar());
        using (var deleted = local.Command("SELECT COUNT(*) FROM image_locations WHERE image_id=7")) Assert.Equal(0L, deleted.ExecuteScalar());
        using (var kept = local.Command("SELECT COUNT(*) FROM image_locations WHERE image_id=8")) Assert.Equal(1L, kept.ExecuteScalar());
        using (var faces = local.Command("SELECT id FROM detected_faces")) Assert.Equal(12L, faces.ExecuteScalar());
        local.UpsertMirroredLocation(new(7, false, "new-local-value", null, null, null, null, null, null));
        Assert.Equal(0, SpaceSyncPublisher.ApplyDownloadedMetadataPage(local, publisherId, new(11, false, [removedClassification])));
        using (var kept = local.Command("SELECT country FROM image_locations WHERE image_id=7")) Assert.Equal("new-local-value", kept.ExecuteScalar());
        Assert.Throws<InvalidDataException>(() => SpaceSyncPublisher.ApplyDownloadedMetadataPage(local, publisherId, new(10, false, [])));
        Assert.Throws<InvalidDataException>(() => SpaceSyncPublisher.ApplyDownloadedMetadataPage(local, publisherId,
            new(12, false, [removedClassification with { ChangeId = 12 }], publisherId, Guid.NewGuid().ToString("N"))));
        var invalidPage = change with { ChangeId = 12, Locations = [], FaceAssignments = [new(999, 999, "confirmed", 1, "user", timestamp)] };
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => SpaceSyncPublisher.ApplyDownloadedMetadataPage(local, publisherId,
            new(12, false, [invalidPage], publisherId, epoch)));
        using (var kept = local.Command("SELECT country FROM image_locations WHERE image_id=7")) Assert.Equal("new-local-value", kept.ExecuteScalar());
        using (var cursor = local.Command("SELECT downloaded_sequence FROM sync_publishers WHERE publisher_id=$publisher"))
        { cursor.Parameters.AddWithValue("$publisher", publisherId); Assert.Equal(11L, cursor.ExecuteScalar()); }
        using (var suppression = local.Command("SELECT COUNT(*) FROM sync_suppression")) Assert.Equal(0L, suppression.ExecuteScalar());
        using (var events = local.Command("SELECT COUNT(*) FROM sync_metadata_changes")) Assert.Equal(eventsBeforeApply, Convert.ToInt64(events.ExecuteScalar()));
    }

    private string ReadPublisherForGlobalImage(string globalId)
    {
        using var query = _database.Command("SELECT publisher_id FROM sync_entity_identities WHERE kind='image' AND global_id=$global");
        query.Parameters.AddWithValue("$global", globalId);
        return Convert.ToString(query.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    [Fact]
    public void EpochChangeRejectsOldPublisherBeforeWriting()
    {
        var publisher = Publisher(Guid.NewGuid().ToString("N"));
        var batch = Images(publisher.Publisher);
        using (var epoch = _database.Command("UPDATE sync_space_state SET epoch='replacement' WHERE id=1")) epoch.ExecuteNonQuery();
        Assert.Throws<InvalidOperationException>(() => publisher.Receiver.Apply(batch));
        Assert.Empty(_database.GetSources());
    }

    public void Dispose()
    {
        _database.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
