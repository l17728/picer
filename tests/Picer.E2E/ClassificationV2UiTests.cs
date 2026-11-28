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
    public async Task GuiStartsJobAndRetriesOnlyFailedPhoto()
    {
        var root = Directory.CreateTempSubdirectory("picer-retry-ui-").FullName;
        var photo = Path.Combine(root, "fixture.png");
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var db = new PicerDatabase(Path.Combine(root, "picer.db"));
        var source = db.AddSource(root);
        db.UpsertImage(new ImageRecord { SourceId = source.Id, Path = photo, FileSize = 69, IndexedUtc = DateTime.UtcNow, ModifiedUtc = DateTime.UtcNow });
        var repo = new ClassificationRepository(db);
        repo.Configure(new(true, "fixture", "local", false));
        var directory = Path.GetDirectoryName(typeof(Picer.App.App).Assembly.Location)!;
        var executable = Environment.GetEnvironmentVariable("PICER_E2E_GUI_EXE") ?? Path.Combine(directory, "Picer.App.exe");
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            Environment = { ["PICER_DATA_ROOT"] = root, ["PICER_BOOTSTRAP_ROOT"] = Path.Combine(root, "bootstrap"),
                ["PICER_LOCAL_MODEL_ENDPOINT"] = $"http://127.0.0.1:{port}/chat" }
        })!;
        try
        {
            AutomationElement Find(string name, ControlType type) => Wait(() => AutomationElement.RootElement.FindFirst(TreeScope.Descendants,
                new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, type),
                    new PropertyCondition(AutomationElement.NameProperty, name))));
            ((ExpandCollapsePattern)Find("AI 功能菜单", ControlType.MenuItem).GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
            ((InvokePattern)Find("照片分类与审核", ControlType.MenuItem).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            ((InvokePattern)Find("开始处理", ControlType.Button).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            Assert.True(SpinWait.SpinUntil(() => repo.LatestJob()?.Failed == 1, TimeSpan.FromSeconds(30)));
            var job = repo.LatestJob()!;
            File.WriteAllBytes(photo, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGNgYGAAAAAEAAH2FzhVAAAAAElFTkSuQmCC"));
            var window = Find("照片分类与审核", ControlType.Window);
            var refresh = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "刷新"));
            ((InvokePattern)refresh.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            var retry = Find("重试本任务失败项", ControlType.Button);
            Assert.True(SpinWait.SpinUntil(() => retry.Current.IsEnabled, TimeSpan.FromSeconds(15)));
            ((InvokePattern)retry.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            await RespondAsync(propose: true);
            Assert.True(SpinWait.SpinUntil(() => repo.Job(job.Id).Succeeded == 1, TimeSpan.FromSeconds(20)));
            Assert.Equal(0, repo.Job(job.Id).Failed);
            Assert.Equal(2, repo.Job(job.Id).UsedCalls);
            ((InvokePattern)refresh.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            var treeItem = Wait(() => window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem)));
            ((SelectionItemPattern)treeItem.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            Assert.NotNull(Wait(() => window.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                new PropertyCondition(AutomationElement.NameProperty, "fixture.png")))));
            ((InvokePattern)Find("新标签建议", ControlType.Button).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            ((InvokePattern)Find("接受并匹配", ControlType.Button).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            await RespondAsync(propose: false);
            var dynamicId = "dynamic." + Assert.Single(repo.Proposals()).Id;
            Assert.True(SpinWait.SpinUntil(() => repo.Browse(new(new([], []))).Counts.GetValueOrDefault(dynamicId) == 1, TimeSpan.FromSeconds(30)));
            Assert.NotNull(Find("代表照片匹配：完成 1，失败或过期 0，待处理 0。", ControlType.Text));
            // Expand the activity branch so UI Automation can reach the new child.
            var activity = Find("活动（1）", ControlType.TreeItem);
            var branch = (ExpandCollapsePattern)activity.GetCurrentPattern(ExpandCollapsePattern.Pattern);
            if (branch.Current.ExpandCollapseState == ExpandCollapseState.Collapsed) branch.Expand();
            var child = Wait(() => activity.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem)));
            if (child.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand)
                && ((ExpandCollapsePattern)expand).Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
                ((ExpandCollapsePattern)expand).Expand();
            ((SelectionItemPattern)Find("露天茶席（1）", ControlType.TreeItem).GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            Assert.NotNull(Find("fixture.png", ControlType.Text));
            ((InvokePattern)Find("全选当前页", ControlType.Button).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            ToggleState RowSelection() => ((TogglePattern)Find("选择", ControlType.CheckBox)
                .GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState;
            Assert.Equal(ToggleState.On, RowSelection());
            ((InvokePattern)Find("清除选择", ControlType.Button).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            Assert.Equal(ToggleState.Off, RowSelection());
            ((InvokePattern)Find("全选当前页", ControlType.Button).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            ((InvokePattern)Find("确认分类", ControlType.Button).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            Assert.True(SpinWait.SpinUntil(() => repo.Browse(new(new([], []))).Photos.Single()
                .ConfirmedIds!.Contains(dynamicId), TimeSpan.FromSeconds(10)));
            Assert.NotNull(Find("已保存 1 张照片的分类决定。", ControlType.Text));
            ((InvokePattern)Find("全选当前页", ControlType.Button).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            ((InvokePattern)Find("不是此类", ControlType.Button).GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            Assert.True(SpinWait.SpinUntil(() => !repo.Browse(new(new([], []))).Photos.Single()
                .LabelIds.Contains(dynamicId), TimeSpan.FromSeconds(10)));

            async Task RespondAsync(bool propose)
            {
                using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
                using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, false, leaveOpen: true);
                int length = 0;
                while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line)
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..]);
                Assert.InRange(length, 1, 2000000);
                // System.Text.Json serializes this fixture's HTTP body as ASCII (including escaped Chinese).
                var body = new char[length];
                await reader.ReadBlockAsync(body, timeout.Token);
                using var payload = System.Text.Json.JsonDocument.Parse(new string(body));
                using var input = System.Text.Json.JsonDocument.Parse(payload.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("text").GetString()!);
                var result = System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = "classification-indexed-v1",
                    inputFingerprint = input.RootElement.GetProperty("inputFingerprint").GetString(), evaluatedAll = true,
                    supported = new[] { propose ? 0 : Array.FindIndex(input.RootElement.GetProperty("labels").EnumerateArray().ToArray(), label => label[1].GetString() == "露天茶席") }, uncertain = Array.Empty<int>(), conflicting = Array.Empty<int>(), proposals = propose ? new[] { new ClassificationProposalDraft("露天茶席", "activity", "户外茶席活动", "单个茶杯不足以确认", ["nav.activity"], ["image:current"]) } : Array.Empty<ClassificationProposalDraft>() });
                var response = System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = result } } } });
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n{response}"), timeout.Token);
            }
        }
        finally
        {
            listener.Stop();
            if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(5000)) process.Kill(true); }
            db.Dispose();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

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
            var repository = new ClassificationRepository(db);
            repository.Configure(new(false, "test-no-network", "local", false));
            var photo = Assert.Single(repository.Browse(new(new([], []))).Photos);
            repository.Decide(new(Guid.NewGuid().ToString("N"), repository.Epoch, "subject.people", "confirm",
                new Dictionary<long, string> { [photo.ImageId] = photo.ContentIdentity },
                new Dictionary<long, string> { [photo.ImageId] = photo.OwnerId }, repository.Taxonomy().Revision,
                new Dictionary<long, string> { [photo.ImageId] = photo.ManualFingerprint! }));
        }
        var directory = Path.GetDirectoryName(typeof(Picer.App.App).Assembly.Location)!;
        var executable = Environment.GetEnvironmentVariable("PICER_E2E_GUI_EXE") ?? Path.Combine(directory, "Picer.App.exe");
        using var process = Process.Start(new ProcessStartInfo(executable)
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
            // This fixture has one confirmed photo. Empty taxonomy branches stay hidden.
            Assert.True(SpinWait.SpinUntil(() => window.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem))
                .Cast<AutomationElement>().Any(item => item.Current.Name.EndsWith("（1）", StringComparison.Ordinal)), TimeSpan.FromSeconds(10)),
                "分类树节点未显示照片计数");
            // Selecting a node only changes the photo list: the tree must keep exactly the same
            // nodes in the same order, and the clicked node stays selected.
            var treeBefore = DescendantNames(window, ControlType.TreeItem);
            Assert.NotEmpty(treeBefore);
            Assert.All(treeBefore, name => Assert.EndsWith("（1）", name));
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
