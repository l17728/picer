using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Picer.App;
using Picer.App.Views;
using Picer.Core.Data;
using Picer.Core.Identity;

namespace Picer.E2E;

public sealed class MetadataConflictsUiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CatalogReviewWindowSubmitsSelectedFingerprintAndKeepsStaleReviewVisible(bool stale)
    {
        var root=Path.Combine(Path.GetTempPath(),"picer-catalog-review-ui-"+Guid.NewGuid().ToString("N"));
        var previousData=Environment.GetEnvironmentVariable("PICER_DATA_ROOT");
        var previousBootstrap=Environment.GetEnvironmentVariable("PICER_BOOTSTRAP_ROOT");
        using var handler=new CatalogReviewHandler(stale);
        using var client=new HttpClient(handler) { BaseAddress=new Uri("https://picer.test/") };
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async()=>
            {
                CatalogConflictsWindow? window=null;
                try
                {
                    Environment.SetEnvironmentVariable("PICER_DATA_ROOT",root);
                    Environment.SetEnvironmentVariable("PICER_BOOTSTRAP_ROOT",Path.Combine(root,"bootstrap"));
                    AppServices.Initialize();window=new CatalogConflictsWindow(true,client);window.Show();
                    var list=Descendants(window).OfType<ListBox>().Single(item=>AutomationProperties.GetAutomationId(item)=="CatalogRetirementConflicts");
                    await UntilAsync(()=>list.Items.Count==1);list.SelectedIndex=0;
                    var keep=Descendants(window).OfType<Button>().Single(item=>AutomationProperties.GetAutomationId(item)=="KeepCatalogAssociation");
                    keep.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await UntilAsync(()=>handler.Submitted is not null && keep.IsEnabled);
                    Assert.True(handler.Submitted!.KeepCurrent);
                    Assert.Equal(handler.Review.CurrentFingerprint,handler.Submitted.Review.CurrentFingerprint);
                    Assert.Equal(handler.Review.Epoch,handler.Submitted.Review.Epoch);
                    Assert.Equal(stale?1:0,list.Items.Count);
                    if(stale) Assert.Contains(Descendants(window).OfType<TextBlock>(),item=>item.Text.Contains("请刷新后重新审核",StringComparison.Ordinal));
                    Assert.Empty(AppServices.Db.GetImages());
                }
                catch(Exception exception) { failure=exception; }
                finally
                {
                    window?.Close();AppServices.Shutdown();
                    Environment.SetEnvironmentVariable("PICER_DATA_ROOT",previousData);
                    Environment.SetEnvironmentVariable("PICER_BOOTSTRAP_ROOT",previousBootstrap);
                    dispatcher.InvokeShutdown();
                }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(90)),"分类删除审核窗口测试超时");
        try { Assert.Null(failure); } finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
    }

    private sealed class CatalogReviewHandler(bool stale) : HttpMessageHandler
    {
        internal CatalogRetirementReview Review { get; }=new("publisher","epoch",
            new(Guid.NewGuid().ToString("N"),"tag",Guid.NewGuid().ToString("N"),"image",Guid.NewGuid().ToString("N"),1),
            "标签：风景",@"C:\照片\山景.jpg","conflict","v2:"+new string('a',64));
        internal CatalogRetirementDecision? Submitted { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            if(request.RequestUri!.AbsolutePath.EndsWith("/resolve",StringComparison.Ordinal))
            {
                Submitted=await request.Content!.ReadFromJsonAsync<CatalogRetirementDecision>(DeviceAuthorization.Json,cancellationToken);
                return new(stale?HttpStatusCode.BadRequest:HttpStatusCode.OK) { Content=JsonContent.Create(new { resolved=!stale }) };
            }
            return new(HttpStatusCode.OK) { Content=JsonContent.Create(Submitted is null || stale?new[]{Review}:Array.Empty<CatalogRetirementReview>()) };
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void RemoteConflictWindowUsesServerRecordsAndNeverWritesItsIdsIntoTheLocalLibrary(int scenario)
    {
        var faceState=scenario is >0 and <5;
        var photoClassification=scenario>=5;
        var videoFace=scenario is 3 or 4;var rejection=scenario is 2 or 4;
        var root = Path.Combine(Path.GetTempPath(), "picer-remote-conflict-ui-" + Guid.NewGuid().ToString("N"));
        using var server = new PicerDatabase(Path.Combine(root, "server", "picer.db"));
        server.UpsertMirroredPerson(new(90,"current",0,0));
        var edit = new MetadataEdit("person",90,"name","current",DateTime.UtcNow.ToString("O"),Guid.NewGuid().ToString("N"));
        if(faceState)
        {
            var source=server.AddSource(@"C:\TestPhotos");
            if(videoFace)
            {
                new Picer.Core.Video.VideoRepository(server).UpsertMirrored(new(70,source.Id,@"C:\TestPhotos\one.mp4",10,DateTime.UnixEpoch,10,100,100,null,null,"active"),source.Id);
                server.UpsertMirroredVideoMetadata(new Picer.Core.Gateway.GatewayMetadata([],[],[]) with
                {
                    VideoKeyframes=[new(75,70,0,"",0)],
                    VideoFaces=[new(80,70,75,0,0,0,0,10,10,0.9,null,null,null,null,null,null,DateTime.UtcNow.ToString("O")) { ModelVersion="test-model" }]
                });
            }
            else
            {
            server.UpsertMirroredImage(new Picer.Core.Models.ImageRecord { Id=70,SourceId=source.Id,Path=@"C:\TestPhotos\one.jpg",
                FileSize=10,ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow });
            server.UpsertMirroredFace(new(80,70,"test-model",0,0,0,10,10,0.9,null,null,DateTime.UtcNow.ToString("O")));
            }
            edit=edit with { Kind=videoFace?"video-face-state":"face-state",EntityId=80,Field="state",
                Value=System.Text.Json.JsonSerializer.Serialize(new FaceManualState(90,"confirmed","test-model")) };
            if(rejection) edit=edit with { Kind=videoFace?"video-face-rejection":"face-rejection",Field="90",
                Value=System.Text.Json.JsonSerializer.Serialize(new FacePersonRejection(true,"test-model")) };
        }
        if(photoClassification)
        {
            var source=server.AddSource(@"C:\TestPhotos");
            server.UpsertMirroredImage(new Picer.Core.Models.ImageRecord { Id=70,SourceId=source.Id,Path=@"C:\TestPhotos\one.jpg",
                FileSize=10,ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow });
            edit=edit with { Kind=scenario==5?"photo-classification-policy":"photo-classification-confirmation",
                EntityId=70,Field="scene.mountain",Value=scenario==5?"rejected":"1" };
        }
        server.AcceptMetadataEdits([edit]);
        server.AcceptMetadataEdits([edit with { Value=photoClassification ? scenario==5?"restored":"0" : rejection
            ? System.Text.Json.JsonSerializer.Serialize(new FacePersonRejection(false,"test-model")) : faceState
            ? System.Text.Json.JsonSerializer.Serialize(new FaceManualState(null,"unassigned","test-model")) : "proposed",
            OperationId=Guid.NewGuid().ToString("N") }]);
        using var client = new HttpClient(new ConflictReviewHandler(server)) { BaseAddress = new Uri("https://picer.test/") };
        var priorData = Environment.GetEnvironmentVariable("PICER_DATA_ROOT");
        var priorBootstrap = Environment.GetEnvironmentVariable("PICER_BOOTSTRAP_ROOT");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                MetadataConflictsWindow? window = null;
                try
                {
                    Environment.SetEnvironmentVariable("PICER_DATA_ROOT",Path.Combine(root,"local"));
                    Environment.SetEnvironmentVariable("PICER_BOOTSTRAP_ROOT",Path.Combine(root,"bootstrap"));
                    AppServices.Initialize();
                    window = new MetadataConflictsWindow(remote:true,client:client); window.Show();
                    var list = Descendants(window).OfType<ListBox>().Single(item => AutomationProperties.GetAutomationId(item)=="MetadataConflicts");
                    await UntilAsync(() => list.Items.Count==1); list.SelectedIndex=0;
                    if(faceState)
                    {
                        await UntilAsync(()=>Descendants(window).OfType<TextBlock>().Any(text=>text.Text.Contains(rejection?"允许推荐此人物":"移回待分类",StringComparison.Ordinal)));
                        Assert.Contains(Descendants(window).OfType<TextBlock>(),text=>text.Text.Contains(rejection?"不再推荐此人物":"确认人物：current",StringComparison.Ordinal));
                    }
                    if(photoClassification)
                        await UntilAsync(()=>Descendants(window).OfType<TextBlock>().Any(text=>text.Text.Contains(
                            scenario==5?"恢复为待确认":"撤回人工确认",StringComparison.Ordinal)));
                    var apply = Descendants(window).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button)=="UseProposedMetadata");
                    apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await UntilAsync(() => list.Items.Count==0);
                    if(photoClassification) Assert.Equal(scenario==5?"restored":"0",Assert.Single(server.GetMetadataEdits()).Value);
                    else if(videoFace) Assert.Null(Assert.Single(server.GetAllMirroredVideoFaces()).AssignedPersonId);
                    else if(faceState) Assert.Empty(server.GetAllMirroredFaceAssignments());
                    else Assert.Equal("proposed",new Picer.Core.People.PeopleService(server).Get(90)!.DisplayName);
                    if(rejection)
                    {
                        using var rejected=server.Command(videoFace?"SELECT COUNT(*) FROM video_face_person_rejections":"SELECT COUNT(*) FROM face_person_rejections");
                        Assert.Equal(0L,rejected.ExecuteScalar());
                    }
                    Assert.Empty(AppServices.Db.GetMetadataEdits());
                    Assert.Null(new Picer.Core.People.PeopleService(AppServices.Db).Get(90));
                }
                catch (Exception exception) { failure=exception; }
                finally
                {
                    window?.Close(); AppServices.Shutdown();
                    Environment.SetEnvironmentVariable("PICER_DATA_ROOT",priorData);
                    Environment.SetEnvironmentVariable("PICER_BOOTSTRAP_ROOT",priorBootstrap);
                    dispatcher.InvokeShutdown();
                }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(90)),"远程同步冲突界面测试超时");
        server.Dispose();
        try { Assert.Null(failure); } finally { Directory.Delete(root,true); }
    }

    private sealed class ConflictReviewHandler(PicerDatabase database) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/resolve",StringComparison.Ordinal))
            {
                var choice=(await request.Content!.ReadFromJsonAsync<MetadataConflictResolution>(DeviceAuthorization.Json,cancellationToken))!;
                database.ResolveMetadataEditConflict(choice.OperationId,choice.UseProposed,choice.ExpectedVersion,choice.ExpectedOperationId,
                    authoritative:true,expectedEpoch:choice.Epoch);
                return new(HttpStatusCode.OK) { Content=JsonContent.Create(new { resolved=true }) };
            }
            return new(HttpStatusCode.OK) { Content=JsonContent.Create(database.GetMetadataConflictSnapshot(),options:DeviceAuthorization.Json) };
        }
    }

    [Fact]
    public void UserCanRemoveASelectedAliasFromPersonDetails()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-alias-ui-" + Guid.NewGuid().ToString("N"));
        var priorData = Environment.GetEnvironmentVariable("PICER_DATA_ROOT");
        var priorBootstrap = Environment.GetEnvironmentVariable("PICER_BOOTSTRAP_ROOT");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                PersonDetailWindow? window = null;
                try
                {
                    Environment.SetEnvironmentVariable("PICER_DATA_ROOT", root);
                    Environment.SetEnvironmentVariable("PICER_BOOTSTRAP_ROOT", Path.Combine(root, "bootstrap"));
                    AppServices.Initialize();
                    var people = new Picer.Core.People.PeopleService(AppServices.Db);
                    var person = people.Create("测试人物", ["旅伴"]);
                    window = new PersonDetailWindow(person.Id, AppServices.Db, people, new Picer.Core.Vision.VisionAnalysisRepository(AppServices.Db));
                    window.Show();
                    var selection = (ComboBox)window.FindName("AliasSelection");
                    await UntilAsync(() => selection.Items.Count == 1);
                    selection.SelectedIndex = 0;
                    var remove = Descendants(window).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "RemovePersonAlias");
                    remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await UntilAsync(() => selection.Items.Count == 0);
                    Assert.DoesNotContain("旅伴", people.Get(person.Id)!.Aliases);
                    Assert.Equal("测试人物", people.Get(person.Id)!.DisplayName);
                    Assert.Contains(AppServices.Db.GetMetadataEdits(), edit => edit.Field == PicerDatabase.AliasField("旅伴") && edit.Value.Length == 0);
                }
                catch (Exception exception) { failure = exception; }
                finally
                {
                    window?.Close(); AppServices.Shutdown();
                    Environment.SetEnvironmentVariable("PICER_DATA_ROOT", priorData);
                    Environment.SetEnvironmentVariable("PICER_BOOTSTRAP_ROOT", priorBootstrap);
                    dispatcher.InvokeShutdown();
                }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "人物别名移除界面测试超时");
        try { Assert.Null(failure); } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UserCanAssignAnAmbiguousVideoDecisionToExactlyOneSource(bool faceDecision)
    {
        var root=Path.Combine(Path.GetTempPath(),"picer-video-owner-ui-"+Guid.NewGuid().ToString("N"));
        using (var database=new PicerDatabase(Path.Combine(root,"picer.db")))
        {
            foreach (var (id,owner) in new[]{(10L,'a'),(20L,'b')})
            {
                var path=Picer.Core.Identity.PublishedMediaPath.Qualify(new string(owner,32),@"C:\Videos");
                var source=database.AddSource(path).Id;
                new Picer.Core.Video.VideoRepository(database).UpsertMirrored(new(id,source,path+"\\clip.mp4",10,DateTime.UnixEpoch,null,0,0,null,null,"active")
                    { Sha256=new string('f',64) },source);
            }
            if(faceDecision)
            {
                database.UpsertMirroredPerson(new(1,"历史测试人物",0,0));
                database.UpsertMirroredVideoMetadata(new Picer.Core.Gateway.GatewayMetadata([],[],[]) with
                { VideoKeyframes=[new(100,10,0,"",0)] });
                using var face=database.Command("""
                    INSERT INTO video_detected_faces(video_id,keyframe_id,timestamp_seconds,ordinal,model_version,
                        box_x,box_y,box_width,box_height,confidence,assigned_person_id,assignment_state,updated_utc,created_utc)
                    SELECT content_id,100,0,0,'test-model',0,0,10,10,1,1,'confirmed',$now,$now FROM videos WHERE id=10;
                    """);
                face.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));face.ExecuteNonQuery();
            }
            else
            {
            var edit=new MetadataEdit("video-concept",10,"[\"animal\",\"manual\"]","accepted",DateTime.UtcNow.ToString("O"),Guid.NewGuid().ToString("N"))
                { VideoContentIdentity="sha256:"+new string('f',64) };
            using var seed=database.Command("INSERT INTO video_metadata_ownership_reviews(operation_id,content_id,kind,field,payload) SELECT $op,content_id,$kind,$field,$payload FROM videos WHERE id=10");
            seed.Parameters.AddWithValue("$op",edit.OperationId); seed.Parameters.AddWithValue("$kind",edit.Kind);
            seed.Parameters.AddWithValue("$field",edit.Field); seed.Parameters.AddWithValue("$payload",System.Text.Json.JsonSerializer.Serialize(edit)); seed.ExecuteNonQuery();
            }
        }
        var priorData=Environment.GetEnvironmentVariable("PICER_DATA_ROOT");
        var priorBootstrap=Environment.GetEnvironmentVariable("PICER_BOOTSTRAP_ROOT");
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async ()=>
            {
                VideoOwnershipReviewWindow? window=null;
                try
                {
                    Environment.SetEnvironmentVariable("PICER_DATA_ROOT",root);
                    Environment.SetEnvironmentVariable("PICER_BOOTSTRAP_ROOT",Path.Combine(root,"bootstrap"));
                    AppServices.Initialize(); window=new VideoOwnershipReviewWindow(); window.Show();
                    var reviews=Descendants(window).OfType<ListBox>().Single(item=>AutomationProperties.GetAutomationId(item)=="VideoOwnershipReviews");
                    var sources=Descendants(window).OfType<ListBox>().Single(item=>AutomationProperties.GetAutomationId(item)=="VideoOwnershipSources");
                    await UntilAsync(()=>reviews.Items.Count==1); reviews.SelectedIndex=0;
                    await UntilAsync(()=>sources.Items.Count==2); sources.SelectedIndex=0;
                    var apply=Descendants(window).OfType<Button>().Single(item=>AutomationProperties.GetAutomationId(item)=="ApplyVideoOwnership");
                    apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await UntilAsync(()=>reviews.Items.Count==0);
                    var metadata=new Picer.Core.Video.VideoSearchMetadata(AppServices.Db);
                    if(faceDecision)
                    {
                        Assert.Contains(10L,metadata.Match([1],[],[]));Assert.DoesNotContain(20L,metadata.Match([1],[],[]));
                    }
                    else
                    {
                        Assert.Contains("animal",metadata.GetConcepts(10)); Assert.Empty(metadata.GetConcepts(20));
                    }
                    Assert.Empty(AppServices.Db.GetVideoOwnershipReviews());
                }
                catch (Exception exception) { failure=exception; }
                finally
                {
                    window?.Close(); AppServices.Shutdown();
                    Environment.SetEnvironmentVariable("PICER_DATA_ROOT",priorData);
                    Environment.SetEnvironmentVariable("PICER_BOOTSTRAP_ROOT",priorBootstrap);
                    dispatcher.InvokeShutdown();
                }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(90)),"视频历史归属审核界面测试超时");
        try { Assert.Null(failure); } finally { Directory.Delete(root,true); }
    }

    [Fact]
    public void UserCanChooseTheirPreservedChangeThroughRealWindow()
    {
        var root=Path.Combine(Path.GetTempPath(),"picer-conflict-ui-"+Guid.NewGuid().ToString("N"));
        using (var database=new PicerDatabase(Path.Combine(root,"picer.db")))
        {
            database.UpsertMirroredPerson(new(1,"initial",0,0));
            var pending=new MetadataEdit("person",1,"name","my choice","2030-01-01T00:00:00.0000000Z",Guid.NewGuid().ToString("N"));
            database.MergeMetadataEdits([pending]);
            database.MergeMetadataEdits([pending with { Value="remote choice",OperationId=Guid.NewGuid().ToString("N"),ServerVersion=1 }]);
            Assert.Single(database.GetMetadataEditConflicts());
        }
        var priorData=Environment.GetEnvironmentVariable("PICER_DATA_ROOT");
        var priorBootstrap=Environment.GetEnvironmentVariable("PICER_BOOTSTRAP_ROOT");
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async ()=>
            {
                MetadataConflictsWindow? window=null;
                try
                {
                    Environment.SetEnvironmentVariable("PICER_DATA_ROOT",root);
                    Environment.SetEnvironmentVariable("PICER_BOOTSTRAP_ROOT",Path.Combine(root,"bootstrap"));
                    AppServices.Initialize(); window=new MetadataConflictsWindow(); window.Show();
                    var list=Descendants(window).OfType<ListBox>().Single(item=>AutomationProperties.GetAutomationId(item)=="MetadataConflicts");
                    await UntilAsync(()=>list.Items.Count==1); list.SelectedIndex=0;
                    var button=Descendants(window).OfType<Button>().Single(item=>AutomationProperties.GetAutomationId(item)=="UseProposedMetadata");
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await UntilAsync(()=>list.Items.Count==0);
                    var resolved=Assert.Single(AppServices.Db.GetMetadataEdits());
                    Assert.Equal("my choice",resolved.Value); Assert.Equal(1,resolved.BaseVersion); Assert.Equal(0,resolved.ServerVersion);
                    Assert.Empty(AppServices.Db.GetMetadataEditConflicts());
                }
                catch (Exception exception) { failure=exception; }
                finally
                {
                    window?.Close(); AppServices.Shutdown();
                    Environment.SetEnvironmentVariable("PICER_DATA_ROOT",priorData);
                    Environment.SetEnvironmentVariable("PICER_BOOTSTRAP_ROOT",priorBootstrap);
                    dispatcher.InvokeShutdown();
                }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(90)),"同步冲突界面测试超时");
        try { Assert.Null(failure); } finally { Directory.Delete(root,true); }
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline=DateTime.UtcNow.AddSeconds(20);
        while (!condition()) { if (DateTime.UtcNow>=deadline) throw new TimeoutException("同步冲突界面未更新"); await Task.Delay(20); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index=0;index<VisualTreeHelper.GetChildrenCount(root);index++)
        {
            var child=VisualTreeHelper.GetChild(root,index); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
