using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using Picer.Core.Classification;
using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.E2E;

public sealed class ClassificationV2UiTests
{
    [Fact]
    public void IndependentGuiExposesTreeTogglePreviewAndScanWithoutModelCalls()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-v2-uia-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using (var db = new PicerDatabase(Path.Combine(root, "picer.db")))
        {
            var source = db.AddSource(root);
            db.UpsertImage(new ImageRecord { SourceId = source.Id, Path = Path.Combine(root, "test-photo.jpg"),
                FileSize = 10, ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow });
            new ClassificationRepository(db).Configure(new(false, "test-no-network", "local", false));
        }
        var directory = Path.GetDirectoryName(typeof(Picer.App.App).Assembly.Location)!;
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(directory, "Picer.App.exe"))
        {
            UseShellExecute = false,
            Environment = { ["PICER_DATA_ROOT"] = root, ["PICER_BOOTSTRAP_ROOT"] = Path.Combine(root, "bootstrap"), ["PICER_MODEL_SERVER_URL"] = "http://127.0.0.1:1" }
        })!;
        try
        {
            var main = Wait(() => AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id)));
            var menu = Wait(() => main.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "AI 功能菜单")));
            ((ExpandCollapsePattern)menu.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            var open = Wait(() =>
            {
                var pattern = (ExpandCollapsePattern)menu.GetCurrentPattern(ExpandCollapsePattern.Pattern);
                if (pattern.Current.ExpandCollapseState == ExpandCollapseState.Collapsed) pattern.Expand();
                // WPF popup menus can be separate automation roots; keep the search scoped to this test process.
                return AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id),
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "PhotoClassificationMenu")));
            });
            ((InvokePattern)open.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            var window = Wait(() => AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id), new PropertyCondition(AutomationElement.AutomationIdProperty, "ClassificationV2Window"))));
            var rootLabel = Wait(() => window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "nav.nature")));
            var setup = Wait(() => window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "ClassificationV2SetupStatus")));
            Assert.Contains("未启用", setup.Current.Name);
            var toggle = (TogglePattern)rootLabel.GetCurrentPattern(TogglePattern.Pattern);
            Assert.Equal(ToggleState.Off, toggle.Current.ToggleState);
            toggle.Toggle();
            Assert.True(SpinWait.SpinUntil(() => toggle.Current.ToggleState == ToggleState.On, TimeSpan.FromSeconds(10)));
            toggle.Toggle();
            var scan = Wait(() => window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "ClassificationV2Scan")));
            ((InvokePattern)scan.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            Assert.True(SpinWait.SpinUntil(() =>
            {
                var status = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "ClassificationV2Status"));
                return status?.Current.Name.Contains("扫描完成（尚未开始打标）", StringComparison.Ordinal) == true;
            }, TimeSpan.FromSeconds(20)));
            Assert.NotNull(window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "ClassificationV2Photos")));
            using var verification = new PicerDatabase(Path.Combine(root, "picer.db"));
            using var jobs = verification.Command("SELECT COUNT(*) FROM classification_v2_jobs");
            Assert.Equal(0L, jobs.ExecuteScalar());
        }
        finally
        {
            if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(5000)) process.Kill(true); }
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static AutomationElement Wait(Func<AutomationElement?> find)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(30))
        {
            try { if (find() is { } value) return value; } catch (ElementNotAvailableException) { }
            Thread.Sleep(100);
        }
        throw new TimeoutException("classification_uia_element_missing");
    }
}
