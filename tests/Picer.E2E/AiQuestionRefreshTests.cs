using System.IO;
using System.Reflection;
using System.Windows.Threading;
using Picer.App;
using Picer.App.ViewModels;

namespace Picer.E2E;

public sealed class AiQuestionRefreshTests
{
    [Fact]
    public void RepeatedRefreshAfterQuestionDoesNotCancelDisposedSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-question-refresh-" + Guid.NewGuid().ToString("N"));
        var previousData = Environment.GetEnvironmentVariable("PICER_DATA_ROOT");
        var previousBootstrap = Environment.GetEnvironmentVariable("PICER_BOOTSTRAP_ROOT");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    Environment.SetEnvironmentVariable("PICER_DATA_ROOT", root);
                    Environment.SetEnvironmentVariable("PICER_BOOTSTRAP_ROOT", Path.Combine(root, "bootstrap"));
                    AppServices.Initialize();
                    var people = new PeopleViewModel(AppServices.Db, AppServices.People,
                        AppServices.VisionRepository, AppServices.VisionAnalysis, AppServices.FaceThumbnails,
                        AppServices.FaceSuggestions, AppServices.UnknownFaceClusters, AppServices.VisionUnavailableReason);
                    using var model = new MainViewModel(AppServices.Db, AppServices.Index, AppServices.Geocoder,
                        people, AppServices.GetClientServices());
                    Assert.True(model.IncludeSuggestedClassifications);
                    model.AiQuestionInput = "烹饪";
                    model.AiQuestionInput = "";
                    Assert.True(model.IncludeSuggestedClassifications);
                    model.IncludeSuggestedClassifications = false;
                    model.AiQuestionInput = "烹饪";
                    model.AiQuestionInput = "";
                    Assert.False(model.IncludeSuggestedClassifications);
                    model.IncludeSuggestedClassifications = true;
                    model.SearchQuery = "烹饪";
                    model.SearchQuery = "";
                    Assert.True(model.IncludeSuggestedClassifications);
                    var source = new CancellationTokenSource();
                    var token = source.Token;
                    typeof(MainViewModel).GetField("_aiQuestionCts", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(model, source);
                    await model.ApplySearchAndFilterAsync();
                    Assert.True(token.IsCancellationRequested);
                    await model.ApplySearchAndFilterAsync();
                    model.CancelAiQuestionCommand.Execute(null);
                    Assert.Empty(model.Images);
                }
                catch (Exception exception) { failure = exception; }
                finally
                {
                    AppServices.Shutdown();
                    Environment.SetEnvironmentVariable("PICER_DATA_ROOT", previousData);
                    Environment.SetEnvironmentVariable("PICER_BOOTSTRAP_ROOT", previousBootstrap);
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "问答后连续刷新超时");
        try { Assert.Null(failure); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
