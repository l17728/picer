using System.IO;
using System.Windows.Threading;
using Picer.App.ViewModels;
using Picer.Core.Data;
using Picer.Core.People;
using Picer.Core.Vision;

namespace Picer.E2E;

public sealed class PeopleThumbnailLifecycleTests
{
    [Fact]
    public void RepeatedIgnoredCategory_DoesNotDisableSharedThumbnailLoader()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-people-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Exception? failure = null;
        var stage = "启动";
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    using var database = new PicerDatabase(Path.Combine(root, "index.db"));
                    stage = "数据库已打开";
                    var repository = new VisionAnalysisRepository(database);
                    var people = new PeopleService(database);
                    var thumbnails = new FaceThumbnailService(Path.Combine(root, "thumbs"));
                    using var content = new MemoryStream(Convert.FromBase64String(
                        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1sAAAAASUVORK5CYII="));
                    var path = await thumbnails.StoreAsync(123, content);
                    stage = "缩略图已缓存";
                    using var viewModel = new PeopleViewModel(database, people, repository, null, thumbnails,
                        new FaceSuggestionEngine(repository, people), new UnknownFaceClusterer(repository), null);
                    for (var iteration = 0; iteration < 3; iteration++)
                    {
                        stage = $"切换分类 {iteration + 1}";
                        await viewModel.ShowIgnoredFacesCommand.ExecuteAsync(null);
                        stage = $"加载缩略图 {iteration + 1}";
                        var face = new UnassignedFaceViewModel(new(123, 1, "unavailable-source.jpg", null, 0,
                            new NormalizedFaceBox(0, 0, 1, 1), 1));
                        stage = $"添加人脸 {iteration + 1}";
                        viewModel.DisplayedFaces.Add(face);
                        stage = $"安排缩略图 {iteration + 1}";
                        viewModel.ScheduleDisplayedFaceThumbnails([0]);
                        stage = $"等待缩略图 {iteration + 1}";
                        var deadline = DateTime.UtcNow.AddSeconds(3);
                        while (!face.HasThumbnail && DateTime.UtcNow < deadline) await Task.Delay(20);
                        stage = $"校验缩略图 {iteration + 1}";
                        Assert.Equal(path, face.FaceThumbnailPath);
                    }
                }
                catch (Exception exception) { failure = exception; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), $"分类切换后测试线程未结束：{stage}");
        try { Assert.Null(failure); }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
