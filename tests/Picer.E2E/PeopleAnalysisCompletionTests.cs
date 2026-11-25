using System.IO;
using System.Reflection;
using System.Windows.Threading;
using Picer.App;
using Picer.App.ViewModels;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Vision;

namespace Picer.E2E;

public sealed class PeopleAnalysisCompletionTests
{
    private sealed class Analyzer : IImageFaceAnalyzer
    {
        public string AnalyzerName => "index-service-face";
        public string ModelVersion => "sface";
        public async Task<FaceAnalysisResult> AnalyzeAsync(ImageRecord image, CancellationToken token)
        {
            await Task.Delay(10, token).ConfigureAwait(false);
            if (image.Path.EndsWith("bad.jpg", StringComparison.Ordinal)) throw new FaceImageInputException("face_image_missing");
            return new FaceAnalysisResult([]) { ModelVersion = ModelVersion };
        }
    }

    [Fact]
    public void RemoteAnalysisKeepsDispatcherAndCompletesGroupingAfterInputFailuresAndCacheHits()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-analysis-completion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    using var database = new PicerDatabase(Path.Combine(root, "index.db"));
                    var source = database.AddSource(root);
                    var images = Enumerable.Range(0, 6).Select(i => database.UpsertImage(new ImageRecord
                    {
                        SourceId = source.Id, Path = Path.Combine(root, i < 5 ? i + "bad.jpg" : "good.jpg"),
                        FileSize = 1, ModifiedUtc = DateTime.UnixEpoch, IndexedUtc = DateTime.UtcNow, Status = ImageStatus.Active
                    })).ToArray();
                    var repository = new VisionAnalysisRepository(database);
                    var people = new PeopleService(database);
                    using var vm = new PeopleViewModel(database, people, repository, null,
                        new FaceThumbnailService(Path.Combine(root, "thumbs")), new FaceSuggestionEngine(repository, people),
                        new UnknownFaceClusterer(repository), null, services: new ClientServices());
                    vm.SelectedFaceModelVersion = "sface";
                    vm.PropertyChanged += (_, _) => Assert.True(dispatcher.CheckAccess(), "人物分析在后台线程修改界面属性");
                    var method = typeof(PeopleViewModel).GetMethod("AnalyzeThroughServiceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var analyzer = new Analyzer();
                    async Task Run(IReadOnlyList<ImageRecord> batch) => await (Task)method.Invoke(vm,
                        [batch, analyzer, new Progress<(int, int)>(), CancellationToken.None])!;
                    await Run(images);
                    Assert.Contains("新分析 1", vm.StatusText);
                    Assert.Contains("失败 5", vm.StatusText);
                    Assert.Contains("自动归类完成", vm.StatusText);
                    Assert.False(vm.IsOrganizing);
                    await Run([images[^1]]);
                    Assert.Contains("跳过 1", vm.StatusText);
                    Assert.Contains("自动归类完成", vm.StatusText);
                }
                catch (Exception exception) { failure = exception; }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "远程分析归类未完成");
        try { Assert.Null(failure); }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
