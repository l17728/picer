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
            // The shipped classification GUI is ClassificationBrowserWindow (title "照片分类与审核"),
            // which hosts ClassificationV2Client; its controls are addressed by their visible labels.
            var window = Wait(() => AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id),
                new PropertyCondition(AutomationElement.NameProperty, "照片分类与审核"),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window))));
            Assert.NotNull(Wait(() => window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "分类树"))));
            // Every tree node carries its photo count; a library without classifications falls
            // back to showing the whole taxonomy with zero counts instead of an empty tree.
            Assert.True(SpinWait.SpinUntil(() => window.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem))
                .Cast<AutomationElement>().Any(item => item.Current.Name.Contains('（')), TimeSpan.FromSeconds(10)),
                "分类树节点未显示照片计数");
            // Selecting a node only changes the photo list: the tree must keep exactly the same
            // nodes in the same order, and the clicked node stays selected.
            var treeBefore = DescendantNames(window, ControlType.TreeItem);
            Assert.NotEmpty(treeBefore);
            var firstNode = window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem));
            Assert.NotNull(firstNode);
            ((SelectionItemPattern)firstNode!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            Thread.Sleep(1500);
            Assert.Equal(treeBefore, DescendantNames(window, ControlType.TreeItem));
            Assert.True(((SelectionItemPattern)firstNode.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected,
                "点击后分类节点失去了选中状态");
            Assert.NotNull(Wait(() => window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List))));
            var suggestions = Wait(() => window.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox),
                new PropertyCondition(AutomationElement.NameProperty, "包含待确认建议"))));
            var toggle = (TogglePattern)suggestions.GetCurrentPattern(TogglePattern.Pattern);
            Assert.Equal(ToggleState.On, toggle.Current.ToggleState);
            toggle.Toggle();
            Assert.True(SpinWait.SpinUntil(() => toggle.Current.ToggleState == ToggleState.Off, TimeSpan.FromSeconds(10)));
            toggle.Toggle();
            Assert.True(SpinWait.SpinUntil(() => toggle.Current.ToggleState == ToggleState.On, TimeSpan.FromSeconds(10)));
            var scan = Wait(() => window.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, "增量扫描"))));
            ((InvokePattern)scan.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            // "增量扫描" only builds a plan: nothing is labelled and no model job is created.
            // The plan runs over the local service and can be slow while the suite loads other
            // app instances, so wait generously and surface the window text on failure.
            var scanFeedback = string.Empty;
            Assert.True(SpinWait.SpinUntil(() =>
            {
                scanFeedback = DescendantNames(window, ControlType.Text).FirstOrDefault(name =>
                    name.StartsWith("扫描计划：", StringComparison.Ordinal) || name.StartsWith("扫描失败：", StringComparison.Ordinal)) ?? "";
                return scanFeedback.Length > 0;
            }, TimeSpan.FromSeconds(45)), $"增量扫描后未出现反馈；窗口文本：{scanFeedback}");
            Assert.StartsWith("扫描计划：", scanFeedback, StringComparison.Ordinal);
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

    private static IEnumerable<string> DescendantNames(AutomationElement root, ControlType controlType)
    {
        try
        {
            return root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, controlType))
                .Cast<AutomationElement>().Select(element => element.Current.Name).ToArray();
        }
        catch (ElementNotAvailableException) { return []; }
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
