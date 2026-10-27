using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Picer.App;
using Picer.App.Views;
using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.E2E;

public sealed class ClassificationOwnershipUiTests
{
    [Fact]
    public void HistoricalRejectionAndStalePreferencesCanBeReviewedThroughTheRealClassificationWindow()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-ownership-ui-" + Guid.NewGuid().ToString("N"));
        using (var database = new PicerDatabase(Path.Combine(root, "picer.db")))
        {
            var source = database.AddSource(root);
            database.UpsertImage(new ImageRecord { SourceId = source.Id, Path = Path.Combine(root, "photo.jpg"),
                Sha256 = new string('a', 64), FileSize = 10, ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow });
            using var seed = database.Command("INSERT INTO image_classification_rejections(content_sha256,concept_id,source_scope,rejected_utc) VALUES($sha,'scene.mountain','vision_model',$now)");
            seed.Parameters.AddWithValue("$sha", new string('a', 64)); seed.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            seed.ExecuteNonQuery();
        }
        var previousData = Environment.GetEnvironmentVariable("PICER_DATA_ROOT");
        var previousBootstrap = Environment.GetEnvironmentVariable("PICER_BOOTSTRAP_ROOT");
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                PhotoClassificationWindow? window = null;
                try
                {
                    Environment.SetEnvironmentVariable("PICER_DATA_ROOT", root);
                    Environment.SetEnvironmentVariable("PICER_BOOTSTRAP_ROOT", Path.Combine(root, "bootstrap"));
                    AppServices.Initialize();
                    window = new PhotoClassificationWindow(); window.Show();
                    var tabs = (TabControl)window.FindName("Tabs");
                    tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(tab => Equals(tab.Tag, "ownership"));
                    var items = (ListView)window.FindName("Items");
                    await UntilAsync(() => items.Items.Count == 1);
                    Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("OwnershipHelp")).Visibility);
                    items.SelectedIndex = 0;
                    var restore = Descendants(window).OfType<Button>().Single(button =>
                        AutomationProperties.GetAutomationId(button) == "RestoreClassification");
                    restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await UntilAsync(() => items.Items.Count == 0);
                    using var reviewed = AppServices.Db.Command("SELECT COUNT(*) FROM classification_rejection_ownership_reviews WHERE decision='restored'");
                    Assert.Equal(1L, reviewed.ExecuteScalar());
                    Assert.False(File.Exists(Path.Combine(root, "photo.jpg")));
                    Descendants(window).OfType<Expander>().Single(item=>Equals(item.Header,"聚类高级设置")).IsExpanded=true;
                    window.UpdateLayout();
                    await UntilAsync(()=>Descendants(window).OfType<Button>().Any(button=>Equals(button.Content,"应用并重建动态分类")));
                    await UntilAsync(()=>((TextBlock)window.FindName("ThresholdHelp")).Tag is float);
                    AppServices.ContentSemanticClusters.SaveSettings(false,0.76f);
                    ((CheckBox)window.FindName("AutomaticThreshold")).IsChecked=true;
                    ((Slider)window.FindName("ClusterThreshold")).Value=0.90;
                    var apply=Descendants(window).OfType<Button>().Single(button=>Equals(button.Content,"应用并重建动态分类"));
                    apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var feedback=(TextBlock)window.FindName("FeedbackText");
                    await UntilAsync(()=>feedback.Text.Contains("你的修改已保留",StringComparison.Ordinal));
                    var settings=AppServices.ContentSemanticClusters.GetSettings();
                    Assert.False(settings.Automatic); Assert.Equal(0.76f,settings.ManualThreshold);
                    Assert.Equal("space-setting",Assert.Single(AppServices.Db.GetMetadataEditConflicts()).Proposed.Kind);
                    var indexedImage=Assert.Single(AppServices.Db.GetImages(includeMissing:true));
                    new Picer.Core.SemanticSearch.ClipSemanticRepository(AppServices.Db).Upsert(indexedImage,"ui-model",new float[]{1,0});
                    await UntilAsync(()=>apply.IsEnabled);
                    apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await UntilAsync(()=>feedback.Text.Contains("分类模型已更新",StringComparison.Ordinal));
                    Assert.True(AppServices.ContentSemanticClusters.GetSettingsForModel("ui-model").Automatic);
                    Assert.False(AppServices.ContentSemanticClusters.GetSettingsForModel(null).Automatic);
                    ((CheckBox)window.FindName("AutomaticThreshold")).IsChecked=false;
                    ((Slider)window.FindName("ClusterThreshold")).Value=0.80;
                    await UntilAsync(()=>apply.IsEnabled);
                    apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await UntilAsync(()=>feedback.Text.Contains("动态分类阈值已应用",StringComparison.Ordinal));
                    Assert.Equal(0.80f,AppServices.ContentSemanticClusters.GetSettingsForModel("ui-model").EffectiveThreshold);
                }
                catch (Exception exception) { failure = exception; }
                finally
                {
                    window?.Close(); AppServices.Shutdown();
                    Environment.SetEnvironmentVariable("PICER_DATA_ROOT", previousData);
                    Environment.SetEnvironmentVariable("PICER_BOOTSTRAP_ROOT", previousBootstrap);
                    dispatcher.InvokeShutdown();
                }
            }));
            Dispatcher.Run();
        });
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(90)), "历史拒绝审核界面测试超时");
        try { Assert.Null(failure); }
        finally { Directory.Delete(root, true); }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("分类审核界面未更新");
            await Task.Delay(20);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
