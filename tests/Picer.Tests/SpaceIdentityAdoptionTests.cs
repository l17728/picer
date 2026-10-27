using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Identity;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Video;
using Picer.IndexService.Identity;

namespace Picer.Tests;

public sealed class SpaceIdentityAdoptionTests
{
    [Fact]
    public void RemoteCandidateClusterInvalidatesDiscoveryAndReturnsOnlyAuthorizedMembers()
    {
        using var remote = new PicerDatabase(":memory:");
        var device = new AuthorizedDevice(Guid.NewGuid().ToString("N"), "account", "space", Guid.NewGuid().ToString("N"),
            "public", "gui", AccountStore.EditorScopes, 1, 1);
        var receiver = new SpaceSyncReceiver(remote, device);
        var publisher = receiver.Register(new(Guid.NewGuid().ToString("N"), device.DeviceId));
        var global = Guid.NewGuid().ToString("N");
        Assert.Equal(200, receiver.Apply(new(publisher.PublisherId, publisher.Epoch, 1, Guid.NewGuid().ToString("N"), "images",
            JsonSerializer.SerializeToElement(new SpaceImageBatch(1, @"C:\Photos", [Image(1)]), DeviceAuthorization.Json), [new("image", 1, global)])).Status);
        var image = Assert.Single(remote.GetImages());
        var scope = new SpaceUploadScope([1], People: true);
        var request = new SpaceIdentityOfferRequest(publisher.PublisherId, publisher.Epoch, scope, SpaceSyncProtocol.MetadataVersion);
        Assert.Empty(receiver.ReadIdentityOffers(request).Offers);
        remote.UpsertMirroredFace(new(901, image.Id, "test-model", 0, 0, 0, 10, 10, 0.9, null, null, DateTime.UtcNow.ToString("O")));
        using var local = LocalPublisher(publisher, global);
        var faceOffer = receiver.ReadIdentityOffers(request);
        SpaceSyncPublisher.ImportIdentityOffers(local, publisher, scope, faceOffer.Offers);
        receiver.AdoptIdentities(new(publisher.PublisherId, publisher.Epoch, scope, Pending(local)));
        Assert.Empty(receiver.ReadIdentityOffers(request).Offers); // Persist a no-new-identities checkpoint.
        Execute(remote, """
            INSERT INTO face_candidate_clusters(id,model_version,created_utc) VALUES(900,'test-model','2026-10-27T00:00:00.0000000Z');
            INSERT INTO face_candidate_members(cluster_id,face_id,similarity) VALUES(900,901,0.9);
            """);
        Assert.Empty(receiver.ReadIdentityOffers(request with { MetadataProtocol = 13 }).Offers);
        Assert.Empty(receiver.ReadIdentityOffers(request with { Scope = scope with { People = false } }).Offers);
        Assert.Empty(receiver.ReadIdentityOffers(request with { Scope = scope with { SourceIds = [99] } }).Offers);
        var offer = Assert.Single(receiver.ReadIdentityOffers(request).Offers);
        Assert.Equal("face-cluster", offer.Kind);
        Assert.Equal(0, offer.Data.Deserialize<GatewayFaceCandidateCluster>(DeviceAuthorization.Json)!.Id);
        Assert.Throws<InvalidDataException>(() => SpaceSyncPublisher.ImportIdentityOffers(local, publisher, scope with { People = false }, [offer]));
        SpaceSyncPublisher.ImportIdentityOffers(local, publisher, scope, [offer]);
        SpaceSyncPublisher.ImportIdentityOffers(local, publisher, scope, [offer]);
        var pending = Pending(local);
        var adopted = Assert.Single(pending.Where(item => item.Kind == "face-cluster"));
        Assert.NotEqual(900, adopted.LocalId);
        receiver.AdoptIdentities(new(publisher.PublisherId, publisher.Epoch, scope, pending));
        var page = receiver.ReadMetadataChanges(0, 100);
        SpaceSyncPublisher.ApplyDownloadedMetadataPage(local, publisher.PublisherId, page, scope);
        using var membership = local.Command("SELECT cluster_id FROM face_candidate_members");
        Assert.Equal(adopted.LocalId, membership.ExecuteScalar());
        Assert.Empty(receiver.ReadIdentityOffers(request).Offers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectionOnlyPersonAndItsWithdrawalHaveScopedAdoptableDependencies(bool withdrawn)
    {
        using var remote=new PicerDatabase(":memory:");
        var device=new AuthorizedDevice(Guid.NewGuid().ToString("N"),"account","space",Guid.NewGuid().ToString("N"),
            "public","gui",AccountStore.EditorScopes,1,1);
        var receiver=new SpaceSyncReceiver(remote,device);var publisher=receiver.Register(new(Guid.NewGuid().ToString("N"),device.DeviceId));
        var global=Guid.NewGuid().ToString("N");
        Assert.Equal(200,receiver.Apply(new(publisher.PublisherId,publisher.Epoch,1,Guid.NewGuid().ToString("N"),"images",
            JsonSerializer.SerializeToElement(new SpaceImageBatch(1,@"C:\Photos",[Image(1)]),DeviceAuthorization.Json),[new("image",1,global)])).Status);
        var image=Assert.Single(remote.GetImages());remote.UpsertMirroredPerson(new(900,"拒绝测试人物",0,0));
        remote.UpsertMirroredFace(new(901,image.Id,"test-model",0,0,0,10,10,0.9,null,null,DateTime.UtcNow.ToString("O")));
        var people=new Picer.Core.People.PeopleService(remote);people.MarkNotThisPerson(901,900);
        if(withdrawn) { people.ConfirmFace(901,900);people.MoveFaceToUnassigned(901); }
        var scope=new SpaceUploadScope([1],Videos:false,People:true);
        Assert.Empty(receiver.ReadIdentityOffers(new(publisher.PublisherId,publisher.Epoch,scope with { People=false })).Offers);
        var offers=receiver.ReadIdentityOffers(new(publisher.PublisherId,publisher.Epoch,scope));Assert.Equal(2,offers.Offers.Count);
        using var local=LocalPublisher(publisher,global);
        SpaceSyncPublisher.ImportIdentityOffers(local,publisher,scope,offers.Offers);
        var pending=Pending(local);receiver.AdoptIdentities(new(publisher.PublisherId,publisher.Epoch,scope,pending));
        var edits=receiver.ReadMetadataEditPage(0,"",null,500,publisher.Epoch).Edits;
        var rejection=Assert.Single(edits.Where(edit=>edit.Kind=="face-rejection"));
        Assert.Equal(Assert.Single(pending.Where(item=>item.Kind=="person")).LocalId.ToString(System.Globalization.CultureInfo.InvariantCulture),rejection.Field);
        Assert.Equal(Assert.Single(pending.Where(item=>item.Kind=="face")).LocalId,rejection.EntityId);
        local.MergeMetadataEdits(edits);
        using var count=local.Command("SELECT COUNT(*) FROM face_person_rejections");Assert.Equal(withdrawn?0L:1L,count.ExecuteScalar());
    }

    [Fact]
    public void VideoFramesAndFacesAdoptDependenciesWithoutCopyingRemoteIdsEmbeddingsOrPaths()
    {
        using var remote = new PicerDatabase(":memory:");
        var device = new AuthorizedDevice(Guid.NewGuid().ToString("N"), "account", "space", Guid.NewGuid().ToString("N"),
            "public", "gui", AccountStore.EditorScopes, 1, 1);
        var receiver = new SpaceSyncReceiver(remote, device);
        var publisher = receiver.Register(new(Guid.NewGuid().ToString("N"), device.DeviceId));
        var videoGlobal = Guid.NewGuid().ToString("N");
        var video = new VideoRecord(1, 1, @"C:\Photos\one.mp4", 10, DateTime.UtcNow, null, 0, 0, null, null, "active")
        { Sha256 = new string('b', 64) };
        Assert.Equal(200, receiver.Apply(new(publisher.PublisherId, publisher.Epoch, 1, Guid.NewGuid().ToString("N"), "videos",
            JsonSerializer.SerializeToElement(new SpaceVideoBatch(1, @"C:\Photos", [video]), DeviceAuthorization.Json), [new("video", 1, videoGlobal)])).Status);
        var remoteVideo = Assert.Single(new VideoRepository(remote).GetAll());
        remote.UpsertMirroredPerson(new(900, "video-person", 0, 0));
        var now = DateTime.UtcNow.ToString("O");
        remote.UpsertMirroredVideoMetadata(new GatewayMetadata([], [], []) with
        {
            VideoKeyframes = [new(901, remoteVideo.Id, 0, @"D:\OtherComputer\private-frame.jpg", 123)],
            VideoFaces = [new(902, remoteVideo.Id, 901, 0, 0, 0, 0, 10, 10, 0.9, [1, 2, 3], null, null, 900, "confirmed", now, now) { ModelVersion = "face-model" }]
        });
        var scope = new SpaceUploadScope([1], Photos: false, People: true);
        var offers = receiver.ReadIdentityOffers(new(publisher.PublisherId, publisher.Epoch, scope));
        Assert.Equal(3, offers.Offers.Count);
        Assert.All(offers.Offers, offer => { Assert.Equal("video", offer.MediaKind); Assert.Equal(videoGlobal, offer.MediaGlobalId); });
        Assert.Equal(string.Empty, Assert.Single(offers.Offers.Where(offer => offer.Kind == "keyframe")).Data.Deserialize<GatewayVideoKeyframe>(DeviceAuthorization.Json)!.Path);
        Assert.Null(Assert.Single(offers.Offers.Where(offer => offer.Kind == "video-face")).Data.Deserialize<SpaceVideoFaceIdentityData>(DeviceAuthorization.Json)!.Face.Embedding);
        Assert.Equal("keyframe", Assert.Single(receiver.ReadIdentityOffers(new(publisher.PublisherId, publisher.Epoch, scope with { People = false })).Offers).Kind);
        using var local = LocalPublisher(publisher, Guid.NewGuid().ToString("N"));
        new VideoRepository(local).UpsertMirrored(video, 1);
        using (var map = local.Command("INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id) VALUES($publisher,'video','1',$global)"))
        { map.Parameters.AddWithValue("$publisher", publisher.PublisherId); map.Parameters.AddWithValue("$global", videoGlobal); map.ExecuteNonQuery(); }
        local.UpsertMirroredVideoMetadata(new GatewayMetadata([], [], []) with
        {
            VideoKeyframes = [new(901, 1, 5, "local-frame.jpg", 456)],
            VideoFaces = [new(902, 1, 901, 5, 0, 0, 0, 10, 10, 0.9, null, null, null, null, null, null, now) { ModelVersion = "local-model" }]
        });
        SpaceSyncPublisher.ImportIdentityOffers(local, publisher, scope, offers.Offers);
        var pending = Pending(local);
        Assert.NotEqual(901, Assert.Single(pending.Where(item => item.Kind == "keyframe")).LocalId);
        Assert.NotEqual(902, Assert.Single(pending.Where(item => item.Kind == "video-face")).LocalId);
        receiver.AdoptIdentities(new(publisher.PublisherId, publisher.Epoch, scope, pending));
        var change = Assert.Single(receiver.ReadMetadataChanges(0, 100).Changes);
        var face = Assert.Single(change.VideoFaces!);
        Assert.Equal(Assert.Single(pending.Where(item => item.Kind == "keyframe")).LocalId, face.KeyframeId);
        Assert.Equal(Assert.Single(pending.Where(item => item.Kind == "person")).LocalId, face.AssignedPersonId);
        Assert.Equal(string.Empty, Assert.Single(change.VideoKeyframes!).Path);
    }

    [Fact]
    public void OwnedCatalogAndFaceAreAdoptedWithoutMergingSameNamePeopleOrLeakingOtherSource()
    {
        using var remote = new PicerDatabase(":memory:");
        var device = new AuthorizedDevice(Guid.NewGuid().ToString("N"), "account", "space", Guid.NewGuid().ToString("N"),
            "public", "gui", AccountStore.EditorScopes, 1, 1);
        var receiver = new SpaceSyncReceiver(remote, device);
        var publisher = receiver.Register(new(Guid.NewGuid().ToString("N"), device.DeviceId));
        var imageGlobal = Guid.NewGuid().ToString("N");
        var batch = new SpaceSyncBatch(publisher.PublisherId, publisher.Epoch, 1, Guid.NewGuid().ToString("N"), "images",
            JsonSerializer.SerializeToElement(new SpaceImageBatch(1, @"C:\Photos", [Image(1)]), DeviceAuthorization.Json), [new("image", 1, imageGlobal)]);
        Assert.Equal(200, receiver.Apply(batch).Status);
        var image = Assert.Single(remote.GetImages());
        remote.UpsertMirroredPerson(new(900, "同名人物", 0, 0));
        remote.UpsertMirroredTag(new Tag { Id = 902, Name = "remote-tag", CreatedUtc = DateTime.UtcNow });
        remote.UpsertMirroredAlbum(new Album { Id = 903, Name = "remote-album", CreatedUtc = DateTime.UtcNow, Description = "description" });
        remote.UpsertMirroredFace(new(901, image.Id, "face-model", 0, 1, 2, 3, 4, 0.9, null, [1, 2, 3], DateTime.UtcNow.ToString("O")));
        Execute(remote, $"""
            INSERT INTO face_person_assignments(face_id,person_id,state,assigned_by,updated_utc) VALUES(901,900,'confirmed','user','2026-10-23');
            INSERT INTO image_tags(image_id,tag_id) VALUES({image.Id},902);
            INSERT INTO album_images(album_id,image_id,added_utc) VALUES(903,{image.Id},'2026-10-23');
            """);
        var foreignDevice = device with { DeviceId = Guid.NewGuid().ToString("N"), GrantId = Guid.NewGuid().ToString("N") };
        var foreign = new SpaceSyncReceiver(remote, foreignDevice);
        var foreignPublisher = foreign.Register(new(Guid.NewGuid().ToString("N"), foreignDevice.DeviceId));
        Assert.Equal(200, foreign.Apply(batch with { PublisherId = foreignPublisher.PublisherId, Sequence = 1,
            OperationId = Guid.NewGuid().ToString("N"), Identities = [new("image", 1, Guid.NewGuid().ToString("N"))] }).Status);
        var foreignImage = remote.GetImages().Single(item => item.Id != image.Id);
        remote.UpsertMirroredTag(new Tag { Id = 904, Name = "private-other-source", CreatedUtc = DateTime.UtcNow });
        Execute(remote, $"INSERT INTO image_tags(image_id,tag_id) VALUES({foreignImage.Id},904)");

        var scope = new SpaceUploadScope([1], People: true);
        var page = receiver.ReadIdentityOffers(new(publisher.PublisherId, publisher.Epoch, scope));
        Assert.Equal(4, page.Offers.Count);
        Assert.All(page.Offers, offer => Assert.Equal(imageGlobal, offer.MediaGlobalId));
        Assert.DoesNotContain(page.Offers, offer => offer.Data.ToString().Contains("private-other-source", StringComparison.Ordinal));
        Assert.Null(Assert.Single(page.Offers.Where(offer => offer.Kind == "face")).Data.Deserialize<GatewayDetectedFace>(DeviceAuthorization.Json)!.Embedding);
        Assert.Equal(page.Offers.Select(offer => offer.OfferId), receiver.ReadIdentityOffers(new(publisher.PublisherId, publisher.Epoch, scope)).Offers.Select(offer => offer.OfferId));
        Assert.Throws<UnauthorizedAccessException>(() => foreign.ReadIdentityOffers(new(publisher.PublisherId, publisher.Epoch, scope)));
        Assert.Equal(2, receiver.ReadIdentityOffers(new(publisher.PublisherId, publisher.Epoch, scope with { People = false })).Offers.Count);
        Assert.Empty(receiver.ReadIdentityOffers(new(publisher.PublisherId, publisher.Epoch, scope with { SourceIds = [99] })).Offers);

        using var local = LocalPublisher(publisher, imageGlobal);
        local.UpsertMirroredPerson(new(1, "同名人物", 0, 0));
        SpaceSyncPublisher.ImportIdentityOffers(local, publisher, scope, page.Offers);
        SpaceSyncPublisher.ImportIdentityOffers(local, publisher, scope, page.Offers); // Retry before acknowledgement.
        using (var people = local.Command("SELECT COUNT(*) FROM people")) Assert.Equal(2L, people.ExecuteScalar());
        var adopted = Pending(local);
        Assert.Equal(4, adopted.Count);
        Assert.NotEqual(1, Assert.Single(adopted.Where(item => item.Kind == "person")).LocalId);
        var adoptedPerson = Assert.Single(adopted.Where(item => item.Kind == "person"));
        using (var label = local.Command("SELECT display_name FROM people WHERE id=$id"))
        {
            label.Parameters.AddWithValue("$id", adoptedPerson.LocalId);
            var name = (string)label.ExecuteScalar()!;
            Assert.NotEqual("同名人物", name);
            Assert.Equal("同名人物", SpaceSyncPublisher.PublishedPersonName(local, publisher.PublisherId, adoptedPerson.LocalId, name));
        }
        var request = new SpaceIdentityAdoptionRequest(publisher.PublisherId, publisher.Epoch, scope, adopted);
        Assert.Throws<ArgumentException>(() => receiver.AdoptIdentities(request with { Items = [null!] }));
        Assert.Equal(adopted, receiver.AdoptIdentities(request).Items);
        Assert.Equal(adopted, receiver.AdoptIdentities(request).Items); // Lost committed response is idempotent.
        var narrowed = receiver.AdoptIdentities(request with { Scope = scope with { People = false } });
        Assert.Equal(2, narrowed.Items.Count);
        Assert.Equal(2, narrowed.Deferred!.Count);
        Assert.All(narrowed.Items, item => Assert.Contains(item.Kind, new[] { "tag", "album" }));
        Assert.All(narrowed.Deferred, item => Assert.Contains(item.Kind, new[] { "face", "person" }));
        Assert.Empty(receiver.ReadIdentityOffers(new(publisher.PublisherId, publisher.Epoch, scope)).Offers);
        var downloaded = receiver.ReadMetadataChanges(0, 100);
        var change = Assert.Single(downloaded.Changes);
        Assert.Equal(Assert.Single(adopted.Where(item => item.Kind == "face")).LocalId, Assert.Single(change.Faces!).Id);
        Assert.Equal(Assert.Single(adopted.Where(item => item.Kind == "person")).LocalId, Assert.Single(change.FaceAssignments!).PersonId);
        Assert.Throws<InvalidOperationException>(() => receiver.AdoptIdentities(request with
        { Items = [adopted[0] with { GlobalId = Guid.NewGuid().ToString("N") }] }));
        using (var checkpoint = remote.Command("SELECT COUNT(*) FROM sync_adoption_scan_checkpoints"))
            Assert.True(Convert.ToInt64(checkpoint.ExecuteScalar()) > 0);
        using (var revision = remote.Command("SELECT revision FROM sync_identity_offer_revision WHERE id=1"))
        {
            var beforeDescription = revision.ExecuteScalar();
            remote.UpsertMirroredVideoMetadata(new GatewayMetadata([], [], []) with
            { CloudVisionAnalyses = [new(image.Id, "test", "model", "prompt", "completed", "description", "{}", DateTime.UtcNow.ToString("O"))] });
            Execute(remote, "UPDATE sync_source_bindings SET original_path=original_path; UPDATE sync_entity_identities SET target_id=target_id");
            Assert.Equal(beforeDescription, revision.ExecuteScalar());
        }
        // An empty-result checkpoint must be invalidated by a newly linked entity.
        remote.UpsertMirroredTag(new Tag { Id = 905, Name = "later-tag", CreatedUtc = DateTime.UtcNow });
        Execute(remote, $"INSERT INTO image_tags(image_id,tag_id) VALUES({image.Id},905)");
        var later = Assert.Single(receiver.ReadIdentityOffers(new(publisher.PublisherId, publisher.Epoch, scope)).Offers);
        var laterAdoption = new SpaceIdentityAdoption(later.OfferId, later.Kind, 10000, later.OfferId);
        Assert.Throws<UnauthorizedAccessException>(() => receiver.AdoptIdentities(request with
        { Items = [laterAdoption, laterAdoption with { OfferId = Guid.NewGuid().ToString("N") }] }));
        using (var rollback = remote.Command("SELECT COUNT(*) FROM sync_entity_identities WHERE global_id=$global"))
        { rollback.Parameters.AddWithValue("$global", later.OfferId); Assert.Equal(0L, rollback.ExecuteScalar()); }
        Execute(remote, $"DELETE FROM image_tags WHERE image_id={image.Id} AND tag_id=905");
        var noLongerOwned = receiver.AdoptIdentities(request with { Items = [laterAdoption] });
        Assert.Empty(noLongerOwned.Items);
        Assert.Equal(laterAdoption, Assert.Single(noLongerOwned.Deferred!));
        for (var index = 0; index < 105; index++)
        {
            remote.UpsertMirroredTag(new Tag { Id = 1000 + index, Name = "page-tag-" + index, CreatedUtc = DateTime.UtcNow });
            using var link = remote.Command("INSERT INTO image_tags(image_id,tag_id) VALUES($image,$tag)");
            link.Parameters.AddWithValue("$image", image.Id); link.Parameters.AddWithValue("$tag", 1000 + index); link.ExecuteNonQuery();
        }
        var firstPage = receiver.ReadIdentityOffers(new(publisher.PublisherId, publisher.Epoch, scope));
        Assert.Equal(100, firstPage.Offers.Count); Assert.True(firstPage.HasMore);
        receiver.AdoptIdentities(request with { Items = firstPage.Offers.Select((offer, index) =>
            new SpaceIdentityAdoption(offer.OfferId, offer.Kind, 20000 + index, offer.OfferId)).ToArray() });
        var lastPage = receiver.ReadIdentityOffers(new(publisher.PublisherId, publisher.Epoch, scope));
        Assert.Equal(5, lastPage.Offers.Count); Assert.False(lastPage.HasMore);
    }

    [Fact]
    public void InvalidOfferRollsBackCatalogIdentityPendingRequestAndSuppressionTogether()
    {
        var publisher = new SpacePublisher(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "device", Guid.NewGuid().ToString("N"), 0);
        var imageGlobal = Guid.NewGuid().ToString("N");
        using var local = LocalPublisher(publisher, imageGlobal);
        var valid = new SpaceIdentityOffer(Guid.NewGuid().ToString("N"), "tag", imageGlobal,
            JsonSerializer.SerializeToElement(new SpaceCatalogIdentityData("tag", DateTime.UtcNow.ToString("O")), DeviceAuthorization.Json));
        Assert.Throws<InvalidDataException>(() => SpaceSyncPublisher.ImportIdentityOffers(local, publisher, new([1]),
            [valid, valid with { OfferId = Guid.NewGuid().ToString("N"), MediaGlobalId = Guid.NewGuid().ToString("N") }]));
        using var count = local.Command("SELECT (SELECT COUNT(*) FROM tags)+(SELECT COUNT(*) FROM sync_pending_identity_adoptions)+(SELECT COUNT(*) FROM sync_suppression)");
        Assert.Equal(0L, count.ExecuteScalar());
    }

    private static PicerDatabase LocalPublisher(SpacePublisher publisher, string imageGlobal)
    {
        var local = new PicerDatabase(":memory:");
        local.AddSource(@"C:\Photos"); local.UpsertMirroredImage(Image(1));
        using var setup = local.Command("""
            INSERT INTO sync_publishers(publisher_id,library_id,incarnation_id,device_grant_id,space_id,state,epoch)
            VALUES($publisher,$library,'device','grant','space','publishing',$epoch);
            INSERT INTO sync_entity_identities(publisher_id,kind,local_key,global_id) VALUES($publisher,'image','1',$image);
            """);
        setup.Parameters.AddWithValue("$publisher", publisher.PublisherId); setup.Parameters.AddWithValue("$library", publisher.LibraryId);
        setup.Parameters.AddWithValue("$epoch", publisher.Epoch); setup.Parameters.AddWithValue("$image", imageGlobal); setup.ExecuteNonQuery();
        return local;
    }
    private static ImageRecord Image(long id) => new() { Id = id, SourceId = 1, Path = @"C:\Photos\one.jpg", FileSize = 10,
        ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow, Sha256 = new string('a', 64), ContentSha256 = new string('a', 64) };
    private static List<SpaceIdentityAdoption> Pending(PicerDatabase database)
    {
        var result = new List<SpaceIdentityAdoption>();
        using var query = database.Command("SELECT offer_id,kind,local_id,global_id FROM sync_pending_identity_adoptions ORDER BY offer_id");
        using var rows = query.ExecuteReader();
        while (rows.Read()) result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetInt64(2), rows.GetString(3)));
        return result;
    }
    private static void Execute(PicerDatabase database, string sql)
    { using var command = database.Command(sql); command.ExecuteNonQuery(); }
}
