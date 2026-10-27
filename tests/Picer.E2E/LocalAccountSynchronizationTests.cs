using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Threading;
using Picer.App;
using Picer.App.Views;
using Picer.App.Infrastructure;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Identity;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.IndexService;
using Picer.IndexService.Identity;

namespace Picer.E2E;

public sealed class LocalAccountSynchronizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalModePublishesAndDownloadsThroughItsOwnPairedAccount(bool multipleSources)
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-local-account-sync-" + Guid.NewGuid().ToString("N"));
        var localRoot = Path.Combine(root, "local");
        var bootstrap = Path.Combine(root, "bootstrap");
        var previous = new[] { "PICER_DATA_ROOT", "PICER_BOOTSTRAP_ROOT", "PICER_INDEX_SYNC_IP", "PICER_INDEX_SERVICE_TOKEN" }
            .ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var origin = "https://127.0.0.1:" + port;
        var instance = AccountServiceHost.Build(Path.Combine(root, "service"), origin, port);
        var credentials = new WindowsDeviceCredentialStore(Path.Combine(bootstrap, "account-device.bin"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(multipleSources?180:90));
        Thread? worker = null;
        try
        {
            Assert.Equal(origin, await IndexServiceHost.StartAsync(instance.App));
            var owner = instance.Accounts.Register("local-sync-user", "test-local-sync-password!", "测试照片空间");
            var space = Assert.Single(instance.Accounts.ListSpaces(owner));
            using var signing = IdentitySigningKey.LoadOrCreate(instance.Accounts.Root, instance.Accounts.ServiceId);
            var pin = Convert.ToBase64String(ServiceSecurity.ComputeSpkiPin(instance.Certificate));
            var signingKey = DeviceAuthorization.ExportPublicKey(signing);
            var connection = credentials.Create(new(2, instance.Accounts.ServiceId, origin, pin, signingKey,
                origin + "/identity/enrollments", origin + "/account"), pin, "本地同步测试", "gui");
            using (var deviceKey = WindowsDeviceCredentialStore.OpenKey(connection))
                instance.Accounts.CreateEnrollment(new(connection.RequestId, DeviceAuthorization.ExportPublicKey(deviceKey), "本地同步测试", "gui"));
            var document = instance.Accounts.ApproveEnrollment(owner, connection.RequestId, space.Id, origin, pin, signing, true, false);
            using (var account = credentials.OpenClient())
            {
                await account.ImportAsync(DeviceAuthorization.WriteFileDocument(document));
                connection = account.Connection;
            }
            var session = connection.Session!;
            Directory.CreateDirectory(localRoot);
            long sourceId;
            long tagId=0;
            using (var local = new PicerDatabase(Path.Combine(localRoot, "picer.db")))
            {
                var source = local.AddSource(Path.Combine(root, "source"));
                Directory.CreateDirectory(source.Path);
                File.WriteAllBytes(Path.Combine(source.Path,"one.jpg"),new byte[10]);
                sourceId = source.Id;
                var now = File.GetLastWriteTimeUtc(Path.Combine(source.Path,"one.jpg"));
                local.UpsertMirroredImage(new ImageRecord { Id = 1, SourceId = source.Id, Path = Path.Combine(source.Path, "one.jpg"),
                    FileSize = 10, ModifiedUtc = now, IndexedUtc = now, Status = ImageStatus.Active });
                local.UpsertMirroredVideoMetadata(new GatewayMetadata([], [], []) with
                {
                    CloudVisionAnalyses = [new(1, "test", "model", "prompt", "completed", "本机初始描述", "{}", now.ToString("O"))]
                });
                if(multipleSources)
                {
                    tagId=SemanticEngine.AddTag(local,"旅行").Id;SemanticEngine.AssignTag(local,1,tagId);
                }
            }
            var preferenceDirectory = Path.Combine(localRoot, "upload-preferences");
            Directory.CreateDirectory(preferenceDirectory);
            File.WriteAllText(Path.Combine(preferenceDirectory,
                DeviceAuthorization.Digest(connection.ServiceId + ":" + session.AccountId + ":" + session.SpaceId) + ".json"),
                JsonSerializer.Serialize(new SpaceUploadScope([sourceId], People: false, PreciseLocation: false)));

            Environment.SetEnvironmentVariable("PICER_DATA_ROOT", localRoot);
            Environment.SetEnvironmentVariable("PICER_BOOTSTRAP_ROOT", bootstrap);
            Environment.SetEnvironmentVariable("PICER_INDEX_SYNC_IP", null);
            Environment.SetEnvironmentVariable("PICER_INDEX_SERVICE_TOKEN", null);
            AppPaths.SaveIndexBackendLocal();

            var completed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                _ = dispatcher.InvokeAsync(async () =>
                {
                Exception? failure = null;
                IndexRepairWindow? inspectionWindow=null;
                CatalogConflictsWindow? catalogWindow=null;
                try
                {
                    AppServices.Initialize();
                    Assert.False(AppServices.ServiceMode);
                    Assert.True(AppServices.LocalSpaceSyncConfigured);
                    Assert.Null(AppServices.SyncClient); // No remote browsing client or legacy token publisher.
                    await AppServices.SyncLocalChangesToRemoteAsync(deadline.Token);
                    var baseline=await AppServices.CaptureSpaceNotificationBaselineAsync(deadline.Token);
                    Assert.NotNull(baseline);
                    AppServices.StartSpaceNotifications(baseline:baseline);
                    using (var remote = new PicerDatabase(Path.Combine(instance.Accounts.SpaceRoot(space.Id), "picer.db")))
                    {
                        var image = Assert.Single(remote.GetImages(includeMissing: true));
                        Assert.Equal("本机初始描述", Assert.Single(remote.GetAllMirroredCloudVisionAnalyses()).Summary);
                        using var edit = remote.Command("UPDATE cloud_vision_analyses SET summary='照片空间修改后的描述',updated_utc=$updated WHERE image_id=$image");
                        edit.Parameters.AddWithValue("$image", image.Id);
                        edit.Parameters.AddWithValue("$updated", DateTime.UtcNow.AddMinutes(1).ToString("O"));
                        edit.ExecuteNonQuery();
                    }
                    while (true)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        using var localRead=AppServices.Db.OpenIndependentConnection();
                        if (Assert.Single(localRead.GetAllMirroredCloudVisionAnalyses()).Summary=="照片空间修改后的描述") break;
                        await Task.Delay(100,deadline.Token);
                    }
                    Assert.Equal("照片空间修改后的描述", Assert.Single(AppServices.Db.GetAllMirroredCloudVisionAnalyses()).Summary);
                    Assert.Equal(sourceId, Assert.Single(AppServices.Db.GetImages(includeMissing: true)).SourceId);
                    using var requester=credentials.OpenClient();
                    var oldAccess=requester.Connection.Session!.AccessToken;
                    var refreshed=await requester.RefreshRejectedAccessAsync(oldAccess,deadline.Token);
                    Assert.Equal(session.GrantId,refreshed.GrantId);
                    Assert.NotEqual(oldAccess,refreshed.AccessToken);
                    inspectionWindow=new IndexRepairWindow(); inspectionWindow.Show();
                    await Task.Delay(50,deadline.Token);
                    var inspect=Descendants(inspectionWindow).OfType<Button>().Single(button=>
                        AutomationProperties.GetAutomationId(button)=="InspectSpaceIndex");
                    inspect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    while (true)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        var status=Descendants(inspectionWindow).OfType<TextBlock>().Single(text=>
                            AutomationProperties.GetAutomationId(text)=="SpaceInspectionStatus");
                        var localStatus=Descendants(inspectionWindow).OfType<TextBlock>().Single(text=>
                            AutomationProperties.GetAutomationId(text)=="IndexInspectionStatus");
                        Assert.DoesNotContain("巡检或同步未完成",localStatus.Text,StringComparison.Ordinal);
                        if (status.Text.StartsWith("空间巡检完成：",StringComparison.Ordinal))
                        {
                            var targets=Descendants(inspectionWindow).OfType<ListBox>().Single(list=>
                                AutomationProperties.GetAutomationId(list)=="SpaceInspectionTargets");
                            var row=Assert.Single(targets.Items.Cast<string>());
                            Assert.Contains("本地同步测试",row); Assert.Contains("已完成",row); Assert.Contains("最近成功",row); break;
                        }
                        Assert.DoesNotContain("空间巡检未全部完成",status.Text,StringComparison.Ordinal);
                        await Task.Delay(250,deadline.Token);
                    }
                    Assert.Equal(new byte[10],File.ReadAllBytes(Path.Combine(root,"source","one.jpg")));
                    if(multipleSources)
                    {
                        await AppServices.StopSpaceNotificationsAsync();
                        async Task<DeviceAccountClient> PairAsync(string name)
                        {
                            var deviceKey=ECDsa.Create(ECCurve.NamedCurves.nistP256);var request=Guid.NewGuid().ToString("N");
                            instance.Accounts.CreateEnrollment(new(request,DeviceAuthorization.ExportPublicKey(deviceKey),name,"gui"));
                            var approved=instance.Accounts.ApproveEnrollment(owner,request,space.Id,origin,pin,signing,true,false);
                            var paired=new DeviceConnection(origin,instance.Accounts.ServiceId,pin,signingKey,"test",request,name,"gui");
                            var client=new DeviceAccountClient(paired,deviceKey,value=>paired=value);
                            await client.ImportAsync(DeviceAuthorization.WriteFileDocument(approved),deadline.Token);return client;
                        }
                        using var sourceBClient=await PairAsync("另一台来源电脑");
                        using var remoteGui=await PairAsync("远程审核 GUI");
                        using var sourceB=new PicerDatabase(Path.Combine(root,"source-b","picer.db"));
                        var bSource=sourceB.AddSource(Path.Combine(root,"source"));
                        sourceB.UpsertMirroredImage(new ImageRecord {Id=1,SourceId=bSource.Id,Path=Path.Combine(root,"source","one.jpg"),
                            FileSize=10,ModifiedUtc=DateTime.UnixEpoch,IndexedUtc=DateTime.UtcNow});
                        var bTag=SemanticEngine.AddTag(sourceB,"旅行").Id;SemanticEngine.AssignTag(sourceB,1,bTag);
                        var bPublisher=new SpaceSyncPublisher(sourceBClient,sourceB,Path.Combine(root,"source-b-snapshots"));
                        var bScope=new SpaceUploadScope([bSource.Id]);await bPublisher.PublishAsync(scope:bScope,cancellationToken:deadline.Token);
                        await bPublisher.PullMetadataChangesAsync(scope:bScope,cancellationToken:deadline.Token);
                        await UiWork.RunDatabaseAsync(()=>SemanticEngine.DeleteTag(AppServices.Db,tagId),deadline.Token);
                        using(var remote=new PicerDatabase(Path.Combine(instance.Accounts.SpaceRoot(space.Id),"picer.db")))
                        {
                            Assert.Equal(2,remote.GetImages().Count);
                            using var own=remote.Command("SELECT e.target_id FROM sync_entity_identities e JOIN sync_publishers p ON p.publisher_id=e.publisher_id WHERE p.device_grant_id=$grant AND e.kind='image'");
                            own.Parameters.AddWithValue("$grant",session.GrantId);var aImage=Convert.ToInt64(own.ExecuteScalar());
                            var aTag=Assert.Single(SemanticEngine.GetImageTags(remote,aImage)).Id;
                            SemanticEngine.RemoveTag(remote,aImage,aTag);SemanticEngine.AssignTag(remote,aImage,aTag);
                        }
                        await AppServices.SyncLocalChangesToRemoteAsync(deadline.Token);
                        catalogWindow=new CatalogConflictsWindow(true,remoteGui.SpaceClient);catalogWindow.Show();
                        var conflicts=Descendants(catalogWindow).OfType<ListBox>().Single(list=>AutomationProperties.GetAutomationId(list)=="CatalogRetirementConflicts");
                        while(conflicts.Items.Count==0) await Task.Delay(100,deadline.Token);
                        Assert.Single(conflicts.Items.Cast<object>());conflicts.SelectedIndex=0;
                        var keep=Descendants(catalogWindow).OfType<Button>().Single(button=>AutomationProperties.GetAutomationId(button)=="KeepCatalogAssociation");
                        keep.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        while(conflicts.Items.Count!=0 || !keep.IsEnabled) await Task.Delay(100,deadline.Token);
                        await AppServices.SyncLocalChangesToRemoteAsync(deadline.Token);
                        await bPublisher.PublishAsync(scope:bScope,cancellationToken:deadline.Token);
                        await bPublisher.PullMetadataChangesAsync(scope:bScope,cancellationToken:deadline.Token);
                        Assert.Single(AppServices.Db.GetImages());Assert.Single(sourceB.GetImages());
                        Assert.Single(SemanticEngine.GetImageTags(AppServices.Db,1));Assert.Single(SemanticEngine.GetImageTags(sourceB,1));
                        Assert.Equal(new byte[10],File.ReadAllBytes(Path.Combine(root,"source","one.jpg")));
                    }
                }
                catch (Exception exception) { failure = exception; }
                finally
                {
                    inspectionWindow?.Close();
                    catalogWindow?.Close();
                    try { await AppServices.StopSpaceNotificationsAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
                    catch (Exception exception) { failure ??= exception; }
                    try { AppServices.Shutdown(); }
                    catch (Exception exception) { failure ??= exception; }
                    completed.TrySetResult(failure);
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
                });
                Dispatcher.Run();
            });
            worker = thread;
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.Null(await completed.Task.WaitAsync(TimeSpan.FromMinutes(multipleSources?4:2)));
        }
        finally
        {
            if (worker is { IsAlive: true })
            {
                deadline.Cancel();
                if (!worker.Join(TimeSpan.FromSeconds(30)))
                    throw new InvalidOperationException("Isolated sync worker did not stop; its temporary environment was retained to protect real application data.");
            }
            foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
            await instance.App.StopAsync();
            await instance.App.DisposeAsync();
            instance.Certificate.Dispose();
            await credentials.ForgetAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
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
