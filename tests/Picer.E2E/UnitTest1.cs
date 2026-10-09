using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using Picer.App.FileOperations;
using Picer.Core.FileOperations;
using Picer.Core.Hashing;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Video;

namespace Picer.E2E;

public sealed class DesktopSmokeTests
{
    [Fact]
    public async Task VideoThumbnail_UsesPosterLoaderInsteadOfDecodingVideoFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-video-poster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var videoPath = Path.Combine(root, "sample.mp4");
        await File.WriteAllBytesAsync(videoPath, [0, 0, 0, 24]);
        var loaderCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var video = new VideoRecord(7, 1, videoPath, 4, DateTime.UtcNow, null, 0, 0, null, null, "active");
        var item = new Picer.App.ViewModels.ImageItemViewModel(video, null, (_, _) =>
        {
            loaderCalled.TrySetResult();
            return Task.FromResult<string?>(null);
        });

        item.EnsureThumbnailLoad();

        await loaderCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { Directory.Delete(root, true); } catch { }
    }

    [Fact]
    public void SuggestedPersonGroup_RemovesProcessedFacesAndUpdatesCount()
    {
        static Picer.Core.Vision.UnassignedFaceRecord Face(long id) => new(
            id, id, $"face-{id}.jpg", null, 0,
            new Picer.Core.Vision.NormalizedFaceBox(0, 0, 1, 1), 0.99f, 7, "测试人物");

        var group = new Picer.App.ViewModels.SuggestedPersonGroupViewModel(
            new Picer.Core.Vision.SuggestedPersonGroupRecord(7, "测试人物", [Face(1), Face(2)]));

        group.RemoveFaces(new HashSet<long> { 1 });
        Assert.Equal(1, group.FaceCount);
        Assert.Equal("测试人物 · 1 张高相似待确认", group.Summary);
        Assert.Equal(2, Assert.Single(group.Faces).FaceId);

        group.RemoveFaces(new HashSet<long> { 2 });
        Assert.Empty(group.Faces);
        Assert.Equal(0, group.FaceCount);
    }

    [Fact]
    public void FaceDetailWindow_ReadOnlyBindingsRenderWithoutDispatcherFailures()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-face-detail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "picer.db");
        SeedLibrary(databasePath, 1);
        SeedPerson(databasePath);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var db = new PicerDatabase(databasePath);
                var repository = new Picer.Core.Vision.VisionAnalysisRepository(db);
                var faceId = repository.GetFaceIds(db.GetImages(includeMissing: false).Single().Id).Single();
                var window = new Picer.App.Views.FaceDetailWindow(db.GetImages(includeMissing: false).Single(), repository,
                    new Picer.Core.People.PeopleService(db), faceId) { ShowInTaskbar = false };
                window.Show();
                window.UpdateLayout();
                window.Close();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "人脸详情窗口布局测试超时");
        try { Assert.Null(failure); }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void VirtualizingFaceGrid_RapidRemovalsDoNotCorruptGenerator()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var items = new System.Collections.ObjectModel.ObservableCollection<int>(Enumerable.Range(0, 300));
                var list = new System.Windows.Controls.ListBox
                {
                    Width = 700, Height = 420, ItemsSource = items,
                    ItemsPanel = new System.Windows.Controls.ItemsPanelTemplate(
                        new System.Windows.FrameworkElementFactory(typeof(Picer.App.Controls.VirtualizingWrapPanel)))
                };
                System.Windows.Controls.ScrollViewer.SetCanContentScroll(list, true);
                System.Windows.Controls.VirtualizingPanel.SetIsVirtualizing(list, true);
                var window = new System.Windows.Window { Width = 720, Height = 460, Content = list, ShowInTaskbar = false };
                window.Show();
                window.UpdateLayout();
                list.ScrollIntoView(items[220]);
                window.UpdateLayout();
                for (var round = 0; round < 6; round++)
                {
                    foreach (var value in items.Skip(30).Take(3).ToArray()) items.Remove(value);
                    window.UpdateLayout();
                }
                Assert.Equal(282, items.Count);
                items.Clear();
                foreach (var value in Enumerable.Range(1000, 22)) items.Add(value);
                window.UpdateLayout();
                Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.True(list.Items.Count > 0 && list.ItemContainerGenerator.ContainerFromIndex(0) is not null,
                    "切换到小候选组后首张人脸没有重新生成容器");
                window.Close();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "连续移除人脸后的虚拟布局测试超时");
        Assert.Null(failure);
    }

    [Fact]
    public void DirectoryBrowser_SourceAlbumsAndTimelineRootsAlwaysResolvePhotos()
    {
        var isolatedData = Path.Combine(Path.GetTempPath(), "picer-directory-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedData);
        SeedDirectoryBrowser(Path.Combine(isolatedData, "picer.db"), isolatedData);
        var executable = Path.Combine(Path.GetDirectoryName(typeof(Picer.App.App).Assembly.Location)!, "Picer.App.exe");
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            Environment =
            {
                ["PICER_DATA_ROOT"] = isolatedData,
                ["PICER_BOOTSTRAP_ROOT"] = Path.Combine(isolatedData, "bootstrap")
            }
        })!;
        try
        {
            var window = WaitForWindow(process.Id, TimeSpan.FromSeconds(15));
            Assert.NotNull(window);
            var directoryTab = window!.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
                .Cast<AutomationElement>().First(item => item.Current.Name.Contains("目录浏览"));
            ((SelectionItemPattern)directoryTab.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            var tree = WaitForElement(window, "目录树", TimeSpan.FromSeconds(5));
            Assert.NotNull(tree);
            Assert.Equal(new[] { "photos", "我的相册", "光锥之内", "太阳系第三行星（地球）" },
                WaitForDirectTreeItemNames(tree!, 4, TimeSpan.FromSeconds(8)));

            AssertDirectChildren(tree, "photos", new[] { "archive" });
            AssertDirectChildren(tree, "我的相册", new[] { "回归相册" });
            AssertDirectChildren(tree, "光锥之内", new[] { "2024年01月" });
            AssertDirectChildren(tree, "太阳系第三行星（地球）", new[] { "未知位置", "中国" });
            AssertDirectChildren(tree, "中国", new[] { "陕西省" });
            AssertDirectChildren(tree, "陕西省", new[] { "西安市" });

            foreach (var nodeName in new[] { "photos", "我的相册", "光锥之内", "2024年01月" })
            {
                var node = WaitForTreeItem(tree!, nodeName, TimeSpan.FromSeconds(8));
                Assert.True(node is not null, $"目录树中未出现节点：{nodeName}");
                if (node!.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandPattern) &&
                    ((ExpandCollapsePattern)expandPattern).Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
                {
                    ((ExpandCollapsePattern)expandPattern).Expand();
                }
                ((SelectionItemPattern)node!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                var grid = window.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.NameProperty, "图片网格"))
                    .Cast<AutomationElement>().First(item => !item.Current.BoundingRectangle.IsEmpty);
                Assert.NotEmpty(WaitForItems(grid, TimeSpan.FromSeconds(8)).Cast<AutomationElement>());
            }
            Assert.Equal(new[] { "photos", "我的相册", "光锥之内", "太阳系第三行星（地球）" },
                WaitForDirectTreeItemNames(tree, 4, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(3000)) process.Kill(true); }
            try { Directory.Delete(isolatedData, true); } catch { }
        }
    }
    [Fact]
    public void AllSafeFeatureWindows_InitializeWithRealApplicationServices()
    {
        var isolatedData = Path.Combine(Path.GetTempPath(), "picer-ui-dialogs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedData);
        SeedLibrary(Path.Combine(isolatedData, "picer.db"), 1);
        var previousDataRoot = Environment.GetEnvironmentVariable("PICER_DATA_ROOT");
        var previousBootstrapRoot = Environment.GetEnvironmentVariable("PICER_BOOTSTRAP_ROOT");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Environment.SetEnvironmentVariable("PICER_DATA_ROOT", isolatedData);
                Environment.SetEnvironmentVariable("PICER_BOOTSTRAP_ROOT", Path.Combine(isolatedData, "bootstrap"));
                Picer.App.AppServices.Initialize();
                var people = new Picer.App.ViewModels.PeopleViewModel(Picer.App.AppServices.Db, Picer.App.AppServices.People,
                    Picer.App.AppServices.VisionRepository, Picer.App.AppServices.VisionAnalysis, Picer.App.AppServices.FaceThumbnails,
                    Picer.App.AppServices.FaceSuggestions, Picer.App.AppServices.UnknownFaceClusters, Picer.App.AppServices.VisionUnavailableReason);
                using var mainViewModel = new Picer.App.ViewModels.MainViewModel(Picer.App.AppServices.Db,
                    Picer.App.AppServices.Index, Picer.App.AppServices.Geocoder, people, Picer.App.AppServices.GetClientServices());
                var windows = new System.Windows.Window[]
                {
                    new Picer.App.StorageSettingsWindow(),
                    new Picer.App.Views.DataProtectionWindow(new Picer.App.DataProtectionService(Picer.App.AppServices.Db, Picer.App.AppPaths.DataRoot)),
                    new Picer.App.CloudVisionSettingsWindow(Picer.App.AppPaths.CloudVisionSettingsPath),
                    new Picer.App.CloudVisionTaskCenterWindow(mainViewModel),
                    new Picer.App.Views.ClipSemanticWindow(Picer.App.AppServices.EnsureClipSemantic(), Picer.App.AppServices.ClipRepository, Picer.App.AppServices.ClipUnavailableReason),
                    new Picer.App.Views.FlorenceValidationWindow(new Picer.Core.Vision.FlorenceRuntimeValidationService(Picer.App.AppPaths.FlorenceModelRoot), Picer.App.AppPaths.FlorenceRuntimeSettingsPath),
                    new Picer.App.Views.FlorenceBatchWindow(Picer.App.AppServices.FlorenceResults, Picer.App.AppPaths.FlorenceModelRoot, Picer.App.AppPaths.FlorenceRuntimeSettingsPath),
                    new Picer.App.Views.DuplicateWindow(new Picer.App.ViewModels.DuplicateViewModel(Picer.App.AppServices.Db, Picer.App.AppServices.Index, Picer.App.AppServices.FileOperations)),
                    new Picer.App.Views.RuntimeComponentsWindow(),
                    new Picer.App.Views.DiagnosticCenterWindow(new Picer.App.DiagnosticService(Picer.App.AppServices.Db, Picer.App.AppServices.Storage, Picer.App.AppServices.Thumbnails, Picer.App.AppPaths.RuntimeRoot, Picer.App.AppPaths.DataRoot))
                };
                foreach (var window in windows)
                {
                    window.Show();
                    window.Dispatcher.Invoke(() => { });
                    Assert.True(window.IsVisible, $"功能窗口未能显示：{window.Title}");
                    window.Close();
                }
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                Picer.App.AppServices.Shutdown();
                Environment.SetEnvironmentVariable("PICER_DATA_ROOT", previousDataRoot);
                Environment.SetEnvironmentVariable("PICER_BOOTSTRAP_ROOT", previousBootstrapRoot);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "功能窗口初始化测试超时");
        try { Assert.Null(failure); }
        finally { try { Directory.Delete(isolatedData, true); } catch { } }
    }

    [Fact]
    public async Task WindowsRecycleBin_ProcessesOnlyApprovedTemporarySnapshot()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "picer-real-recycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "approved-test-file.bin");
        await File.WriteAllBytesAsync(path, [0x50, 0x49, 0x43, 0x45, 0x52]);
        var info = new FileInfo(path);
        var selection = new AuthorizedFileSelection(1, path, info.Length, info.LastWriteTimeUtc, FileHasher.ComputeSha256(path));

        var result = await new WindowsRecycleBinFileOperation().MoveToRecycleBinAsync(
            new DeletionPlan(Guid.NewGuid(), DateTime.UtcNow, [selection]));

        Assert.Equal(1, result.SucceededCount);
        Assert.False(File.Exists(path));
        try { Directory.Delete(root); } catch { }
    }

    [Fact]
    public void MainWindow_ExposesCoreNavigationAndSearchToUiAutomation()
    {
        var isolatedData = Path.Combine(Path.GetTempPath(), "picer-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedData);
        SeedLibrary(Path.Combine(isolatedData, "picer.db"), 100_000);
        var appDirectory = Path.GetDirectoryName(typeof(Picer.App.App).Assembly.Location)!;
        var executable = Path.Combine(appDirectory, "Picer.App.exe");
        var firstWindowBudget = TimeSpan.FromSeconds(Environment.ProcessorCount <= 4 ? 20 : 4);
        var measurements = new List<TimeSpan>();
        AutomationElement? window = null;
        Process? process = null;
        for (var attempt = 1; attempt <= 3 && window is null; attempt++)
        {
            process?.Dispose();
            process = new Process();
            var startup = Stopwatch.StartNew();
            process.StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                Environment =
                {
                    ["PICER_DATA_ROOT"] = isolatedData,
                    ["PICER_BOOTSTRAP_ROOT"] = Path.Combine(isolatedData, "bootstrap")
                }
            };
            process.Start();
            try
            {
                window = WaitForWindow(process.Id, TimeSpan.FromSeconds(15));
                startup.Stop();
                measurements.Add(startup.Elapsed);
                if (window is not null && startup.Elapsed < firstWindowBudget) break;
            }
            catch
            {
                startup.Stop();
                measurements.Add(startup.Elapsed);
            }
            window = null;
            process.Refresh();
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(3000)) process.Kill(true);
            }
            Thread.Sleep(300);
        }
        try
        {
            Assert.NotNull(process);
            Assert.NotNull(window);
            Assert.True(measurements.Any(m => m < firstWindowBudget),
                $"首窗三次测量均超 {firstWindowBudget.TotalSeconds:F0}s 预算：{string.Join(", ", measurements.Select(m => $"{m.TotalMilliseconds:F0}ms"))}");
            Assert.NotNull(window!.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, "搜索图片")));
            Assert.NotNull(window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, "图片网格")));
            Assert.NotNull(window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, "添加文件夹")));
            AssertMenuEntries(window, "文件菜单", "添加文件夹", "存储设置", "删除所选", "备份与恢复");
            AssertMenuEntries(window, "AI 功能菜单", "AI 模型设置", "描述选中照片", "任务中心",
                "中文语义检索", "Florence 运行验证", "描述目录中的照片和视频…", "继续目录描述任务…");
            AssertMenuEntries(window, "工具菜单", "增量更新索引", "检查缺失", "查找重复", "运行组件", "诊断中心", "扫描视频");
            Assert.Contains("Picer", window.Current.Name);
            var tabs = window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
            var searchTab = tabs.Cast<AutomationElement>().FirstOrDefault(item => item.Current.Name.Contains("搜索结果"));
            Assert.Contains(tabs.Cast<AutomationElement>(), item => item.Current.Name.Contains("目录浏览"));
            Assert.Contains(tabs.Cast<AutomationElement>(), item => item.Current.Name.Contains("人物"));
            Assert.Contains(tabs.Cast<AutomationElement>(), item => item.Current.Name.Contains("标签与相册"));
            Assert.Contains(tabs.Cast<AutomationElement>(), item => item.Current.Name.Contains("视频"));
            if (searchTab?.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionObject) == true)
                ((SelectionItemPattern)selectionObject).Select();
            var grid = window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, "图片网格"))!;
            var items = WaitForItems(grid, TimeSpan.FromSeconds(10));
            if (items.Count is 0 or > 100) items = WaitForItems(grid, TimeSpan.FromSeconds(10));
            Assert.InRange(items.Count, 1, 100);
            if (grid.TryGetCurrentPattern(ScrollPattern.Pattern, out var scrollObject) && scrollObject is ScrollPattern scroll)
            {
                for (var i = 0; i < 20; i++) scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
                Thread.Sleep(300);
                Assert.InRange(grid.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Count, 1, 100);
            }
            process!.Refresh();
            var workingSetBudgetMb = Environment.ProcessorCount <= 4 ? 600 : 300;
            Assert.True(process.WorkingSet64 < workingSetBudgetMb * 1024 * 1024, $"滚动后工作集 {process.WorkingSet64 / 1024 / 1024} MB（预算 {workingSetBudgetMb} MB）");
            if (window.TryGetCurrentPattern(TransformPattern.Pattern, out var transformObject) &&
                transformObject is TransformPattern transform && transform.Current.CanResize)
            {
                transform.Resize(900, 560);
                Thread.Sleep(250);
                var search = window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "搜索图片"));
                Assert.False(search!.Current.BoundingRectangle.IsEmpty);
                Assert.True(window.Current.BoundingRectangle.Contains(search.Current.BoundingRectangle.TopLeft));
            }
            var mediaType = WaitForElement(window, "媒体类型过滤", TimeSpan.FromSeconds(3));
            Assert.NotNull(mediaType);
            Assert.True(mediaType!.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var mediaExpand));
            ((ExpandCollapsePattern)mediaExpand).Expand();
            var videoChoice = mediaType.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
                .Cast<AutomationElement>().First(item => item.Current.Name == "视频");
            ((SelectionItemPattern)videoChoice.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            ((ExpandCollapsePattern)mediaExpand).Collapse();
            var directoryTab = tabs.Cast<AutomationElement>().First(item => item.Current.Name.Contains("目录浏览"));
            var videoTab = tabs.Cast<AutomationElement>().First(item => item.Current.Name.Contains("视频"));
            Assert.True(((SelectionItemPattern)directoryTab.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected,
                "媒体筛选选择视频后应停留在目录浏览");
            Assert.False(((SelectionItemPattern)videoTab.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected,
                "媒体筛选不应导航到视频工作台");
        }
        finally
        {
            if (process is not null && !process.HasExited)
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(3000)) process.Kill(true);
            }
            process?.Dispose();
            try { Directory.Delete(isolatedData, true); } catch { }
        }
    }

    [Fact]
    public void PeoplePage_ShowsImmediatelyAndExposesClassificationWorkflow()
    {
        var isolatedData = Path.Combine(Path.GetTempPath(), "picer-people-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedData);
        var databasePath = Path.Combine(isolatedData, "picer.db");
        SeedLibrary(databasePath, 2);
        SeedPerson(databasePath);
        SeedSecondPerson(databasePath);
        var appDirectory = Path.GetDirectoryName(typeof(Picer.App.App).Assembly.Location)!;
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(appDirectory, "Picer.App.exe"))
        {
            UseShellExecute = false,
            Environment =
            {
                ["PICER_DATA_ROOT"] = isolatedData,
                ["PICER_BOOTSTRAP_ROOT"] = Path.Combine(isolatedData, "bootstrap")
            }
        })!;
        try
        {
            var window = WaitForWindow(process.Id, TimeSpan.FromSeconds(15));
            Assert.NotNull(window);
            var peopleTab = window!.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
                .Cast<AutomationElement>().First(item => item.Current.Name.Contains("人物"));
            Assert.True(peopleTab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection));
            var visible = Stopwatch.StartNew();
            ((SelectionItemPattern)selection).Select();
            var skeletonBudget = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
                ? TimeSpan.FromSeconds(4)
                : TimeSpan.FromSeconds(2);
            var pending = WaitForElement(window, "待处理人脸", skeletonBudget);
            Assert.NotNull(pending);
            Assert.True(visible.Elapsed < skeletonBudget, $"人物页骨架显示耗时 {visible.Elapsed}");

            var peopleList = WaitForElement(window, "已命名人物下拉列表", TimeSpan.FromSeconds(5));
            Assert.NotNull(peopleList);
            Assert.True(peopleList!.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var peopleExpand));
            ((ExpandCollapsePattern)peopleExpand).Expand();
            var personItem = WaitForPersonItem(window, process.Id, "测试人物", TimeSpan.FromSeconds(5));
            Assert.NotNull(personItem);
            Assert.True(ActivateAutomationItem(personItem!), "已命名人物下拉项无法通过 UI 自动化选择");
            ((ExpandCollapsePattern)peopleExpand).Collapse();

            var faces = WaitForElement(window, "人物人脸列表", TimeSpan.FromSeconds(5));
            Assert.NotNull(faces);
            Assert.Contains("双击打开关联照片", faces!.Current.HelpText);
            Assert.Contains("右击", faces.Current.HelpText);
            Assert.Contains("Delete", faces.Current.HelpText);
            Assert.NotEmpty(WaitForItems(faces, TimeSpan.FromSeconds(5)).Cast<AutomationElement>());

            var peopleListForSecondSelection = WaitForElement(window, "已命名人物下拉列表", TimeSpan.FromSeconds(3));
            Assert.NotNull(peopleListForSecondSelection);
            Assert.True(peopleListForSecondSelection!.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var peopleExpandForSecondSelection));
            ((ExpandCollapsePattern)peopleExpandForSecondSelection).Expand();
            // Match the exact seeded identity in this ComboBox. Desktop-wide
            // item ordering can include unrelated windows or a stale popup.
            var secondPersonItem = WaitForPersonItem(window, process.Id, "切换测试人物", TimeSpan.FromSeconds(5));
            Assert.NotNull(secondPersonItem);
            Assert.True(ActivateAutomationItem(secondPersonItem!), "第二个已命名人物无法通过 UI 自动化选择");
            ((ExpandCollapsePattern)peopleExpandForSecondSelection).Collapse();
            // The selected person's group title is localized and is not
            // consistently exposed by UIA; the refreshed face list is the
            // stable behavioral assertion.
            Assert.NotEmpty(WaitForItems(faces, TimeSpan.FromSeconds(5)).Cast<AutomationElement>());

            var newPersonName = WaitForElement(window, "新人物名称", TimeSpan.FromSeconds(3));
            var createPerson = WaitForElement(window, "只新建人物", TimeSpan.FromSeconds(3));
            Assert.NotNull(newPersonName);
            Assert.NotNull(createPerson);
            ((ValuePattern)newPersonName!.GetCurrentPattern(ValuePattern.Pattern)).SetValue("空人物回归");
            ((InvokePattern)createPerson!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            Assert.NotNull(WaitForElementContaining(window, ControlType.Text, "已创建人物", TimeSpan.FromSeconds(5)));
            // Re-acquire the ComboBox after the ObservableCollection update;
            // an already-open WPF popup keeps a stale automation subtree.
            var refreshedPeopleList = WaitForElement(window, "已命名人物下拉列表", TimeSpan.FromSeconds(3));
            Assert.NotNull(refreshedPeopleList);
            Assert.True(refreshedPeopleList!.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var refreshedExpand));
            ((ExpandCollapsePattern)refreshedExpand).Expand();
            Assert.True(WaitForPersonItem(window, process.Id, "空人物回归", TimeSpan.FromSeconds(5)) is not null,
                "新建空人物后没有立即出现在已命名人物下拉列表");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(3000)) process.Kill(true);
            }
            try { Directory.Delete(isolatedData, true); } catch { }
        }
    }

    [Fact]
    public void AiQuestion_UsesDedicatedExplicitSubmitPanel()
    {
        var isolatedData = Path.Combine(Path.GetTempPath(), "picer-ai-question-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedData);
        var appDirectory = Path.GetDirectoryName(typeof(Picer.App.App).Assembly.Location)!;
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(appDirectory, "Picer.App.exe"))
        {
            UseShellExecute = false,
            Environment =
            {
                ["PICER_DATA_ROOT"] = isolatedData,
                ["PICER_BOOTSTRAP_ROOT"] = Path.Combine(isolatedData, "bootstrap")
            }
        })!;
        try
        {
            var window = WaitForWindow(process.Id, TimeSpan.FromSeconds(15)); Assert.NotNull(window);
            var button = WaitForElement(window!, "AI 问答按钮", TimeSpan.FromSeconds(5)); Assert.NotNull(button);
            Assert.True(button!.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle));
            ((TogglePattern)toggle).Toggle();
            var input = WaitForElement(window!, "AI 问题输入框", TimeSpan.FromSeconds(5)); Assert.NotNull(input);
            Assert.Equal(ControlType.Edit, input!.Current.ControlType);
            var submit = window!.FindAll(TreeScope.Descendants,
                    new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                        new PropertyCondition(AutomationElement.NameProperty, "提交问题")))
                .Cast<AutomationElement>().FirstOrDefault();
            Assert.NotNull(submit);
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            try { Directory.Delete(isolatedData, true); } catch { }
        }
    }

    private static AutomationElement? WaitForWindow(int processId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
        while (DateTime.UtcNow < deadline)
        {
            var window = AutomationElement.RootElement.FindFirst(TreeScope.Children, condition);
            if (window is not null) return window;
            Thread.Sleep(100);
        }
        return null;
    }

    private static AutomationElement? WaitForElement(AutomationElement root, string name, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var condition = new PropertyCondition(AutomationElement.NameProperty, name);
        do
        {
            var element = root.FindFirst(TreeScope.Descendants, condition);
            if (element is not null) return element;
            Thread.Sleep(50);
        } while (DateTime.UtcNow < deadline);
        return null;
    }

    private static AutomationElement? WaitForElementContaining(
        AutomationElement root, ControlType controlType, string text, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, controlType);
        do
        {
            var element = root.FindAll(TreeScope.Descendants, condition)
                .Cast<AutomationElement>()
                .FirstOrDefault(value => value.Current.Name.Contains(text, StringComparison.Ordinal));
            if (element is not null) return element;
            Thread.Sleep(50);
        } while (DateTime.UtcNow < deadline);
        return null;
    }

    private static AutomationElement? WaitForTreeItem(AutomationElement tree, string text, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            var item = tree.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem))
                .Cast<AutomationElement>().FirstOrDefault(value => value.Current.Name.Contains(text, StringComparison.Ordinal));
            if (item is not null) return item;
            Thread.Sleep(100);
        } while (DateTime.UtcNow < deadline);
        return null;
    }

    private static string[] WaitForDirectTreeItemNames(AutomationElement parent, int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        string[] names;
        do
        {
            names = parent.FindAll(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem))
                .Cast<AutomationElement>().Select(item => item.Current.Name).ToArray();
            if (names.Length == count) return names;
            Thread.Sleep(100);
        } while (DateTime.UtcNow < deadline);
        return names;
    }

    private static void AssertDirectChildren(AutomationElement tree, string parentName, string[] expectedNames)
    {
        var parent = WaitForTreeItem(tree, parentName, TimeSpan.FromSeconds(5));
        Assert.True(parent is not null, $"目录树中未出现父节点：{parentName}");
        if (parent!.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern) &&
            ((ExpandCollapsePattern)pattern).Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
            ((ExpandCollapsePattern)pattern).Expand();
        Assert.Equal(expectedNames, WaitForDirectTreeItemNames(parent, expectedNames.Length, TimeSpan.FromSeconds(5)));
    }

    private static AutomationElement? WaitForControlContainingName(
        AutomationElement root, ControlType controlType, string text, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            var item = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, controlType))
                .Cast<AutomationElement>().FirstOrDefault(value => value.Current.Name.Contains(text, StringComparison.Ordinal));
            if (item is not null) return item;
            Thread.Sleep(100);
        } while (DateTime.UtcNow < deadline);
        return null;
    }

    private static bool ActivateAutomationItem(AutomationElement item)
    {
        if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
        {
            ((SelectionItemPattern)selection).Select();
            return true;
        }
        if (item.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
            return true;
        }
        return false;
    }

    private static AutomationElement? WaitForPersonItem(AutomationElement window, int processId, string name, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        var comboCondition = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox),
            new PropertyCondition(AutomationElement.NameProperty, "已命名人物下拉列表"));
        var itemCondition = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
            new PropertyCondition(AutomationElement.NameProperty, name),
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId));
        do
        {
            try
            {
                // RefreshAsync can replace item containers after the previous
                // expansion. Reacquire and restore the popup's actual state.
                var combo = window.FindFirst(TreeScope.Descendants, comboCondition);
                if (combo is not null && combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandable))
                {
                    var expand = (ExpandCollapsePattern)expandable;
                    if (expand.Current.ExpandCollapseState != ExpandCollapseState.Expanded)
                    {
                        combo.SetFocus();
                        expand.Expand();
                    }
                    var ownerId = combo.GetRuntimeId();
                    AutomationElement? FindOwnedItem(AutomationElement scope)
                    {
                        foreach (AutomationElement item in scope.FindAll(TreeScope.Descendants, itemCondition))
                        {
                            if (!item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)) continue;
                            var container = ((SelectionItemPattern)pattern).Current.SelectionContainer;
                            if (container is not null && container.GetRuntimeId().SequenceEqual(ownerId)) return item;
                        }
                        return null;
                    }
                    var owned = FindOwnedItem(combo);
                    if (owned is not null) return owned;
                    // WPF Popup may have its own HWND outside the main window's
                    // automation subtree. Search only this process's roots and
                    // require the same ComboBox selection container.
                    foreach (AutomationElement root in AutomationElement.RootElement.FindAll(TreeScope.Children,
                        new PropertyCondition(AutomationElement.ProcessIdProperty, processId)))
                    {
                        if (timer.Elapsed >= timeout) break;
                        owned = FindOwnedItem(root);
                        if (owned is not null) return owned;
                    }
                }
            }
            catch (ElementNotAvailableException) { /* collection refresh replaced a UIA peer */ }
            catch (InvalidOperationException) { /* popup collapsed between the state read and expansion */ }
            Thread.Sleep(100);
        } while (timer.Elapsed < timeout);
        return null;
    }

    private static AutomationElementCollection WaitForItems(AutomationElement grid, TimeSpan timeout)
    {
        var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);
        var deadline = DateTime.UtcNow + timeout;
        AutomationElementCollection items;
        do
        {
            items = grid.FindAll(TreeScope.Descendants, condition);
            if (items.Count > 0) return items;
            Thread.Sleep(100);
        } while (DateTime.UtcNow < deadline);
        return items;
    }

    private static void AssertMenuEntries(AutomationElement window, string menuName, params string[] entries)
    {
        var menu = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, menuName));
        Assert.NotNull(menu);
        Assert.True(menu!.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern));
        var missing = entries.ToList();
        for (var round = 0; round < 3 && missing.Count > 0; round++)
        {
            ((ExpandCollapsePattern)pattern).Expand();
            foreach (var entry in missing.ToArray())
                if (WaitForElement(AutomationElement.RootElement, entry, TimeSpan.FromSeconds(3)) is not null)
                    missing.Remove(entry);
            ((ExpandCollapsePattern)pattern).Collapse();
        }
        Assert.True(missing.Count == 0, $"{menuName} 缺少菜单项：{string.Join("、", missing)}");
    }

    private static void SeedLibrary(string path, int count)
    {
        using var db = new PicerDatabase(path);
        var source = db.AddSource(Path.Combine(Path.GetDirectoryName(path)!, "photos"));
        db.Command("DROP TRIGGER IF EXISTS images_ai").ExecuteNonQuery();
        using var transaction = db.BeginTransaction();
        using var command = db.Command("""
            INSERT INTO images(source_id,path,file_size,modified_utc,sha256,width,height,status,indexed_utc,ahash,dhash)
            VALUES($source,$path,100,$utc,NULL,1920,1080,'active',$utc,0,0)
            """);
        command.Transaction = transaction;
        command.Parameters.Add("$source", Microsoft.Data.Sqlite.SqliteType.Integer).Value = source.Id;
        command.Parameters.Add("$path", Microsoft.Data.Sqlite.SqliteType.Text);
        command.Parameters.Add("$utc", Microsoft.Data.Sqlite.SqliteType.Text).Value = DateTime.UtcNow.ToString("O");
        for (var i = 0; i < count; i++)
        {
            command.Parameters["$path"].Value = Path.Combine(source.Path, $"photo-{i:D6}.jpg");
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void SeedPerson(string path)
    {
        using var db = new PicerDatabase(path);
        using var image = db.Command("SELECT id FROM images ORDER BY id LIMIT 1");
        var imageId = (long)image.ExecuteScalar()!;
        using var insert = db.Command("""
            INSERT INTO detected_faces(image_id,model_version,ordinal,box_x,box_y,box_width,box_height,confidence,created_utc)
            VALUES($image,'ui-test',0,0.2,0.2,0.3,0.3,0.99,$now);
            SELECT last_insert_rowid();
            """);
        insert.Parameters.AddWithValue("$image", imageId);
        insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        var faceId = (long)insert.ExecuteScalar()!;
        var people = new Picer.Core.People.PeopleService(db);
        people.ConfirmFace(faceId, people.Create("测试人物").Id);
    }

    private static void SeedSecondPerson(string path)
    {
        using var db = new PicerDatabase(path);
        using var image = db.Command("SELECT id FROM images ORDER BY id LIMIT 1 OFFSET 1");
        var imageId = (long)image.ExecuteScalar()!;
        using var insert = db.Command("""
            INSERT INTO detected_faces(image_id,model_version,ordinal,box_x,box_y,box_width,box_height,confidence,created_utc)
            VALUES($image,'ui-switch-test',0,0.15,0.15,0.4,0.4,0.98,$now);
            SELECT last_insert_rowid();
            """);
        insert.Parameters.AddWithValue("$image", imageId);
        insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        var faceId = (long)insert.ExecuteScalar()!;
        var people = new Picer.Core.People.PeopleService(db);
        people.ConfirmFace(faceId, people.Create("切换测试人物").Id);
    }

    private static void SeedDirectoryBrowser(string path, string root)
    {
        var photos = Path.Combine(root, "photos"); Directory.CreateDirectory(photos);
        var archive = Path.Combine(photos, "archive"); Directory.CreateDirectory(archive);
        File.WriteAllBytes(Path.Combine(archive, "nested.jpg"), [0x01, 0x02, 0x03]);
        using var db = new PicerDatabase(path);
        var source = db.AddSource(photos);
        var records = Enumerable.Range(1, 3).Select(index => db.UpsertImage(new ImageRecord
        {
            SourceId = source.Id, Path = Path.Combine(photos, $"timeline-{index}.jpg"), FileSize = 100,
            ModifiedUtc = new DateTime(2024, 1, index, 12, 0, 0, DateTimeKind.Utc),
            TakenUtc = new DateTime(2024, 1, index, 12, 0, 0, DateTimeKind.Utc),
            IndexedUtc = DateTime.UtcNow, Status = ImageStatus.Active
        })).ToArray();
        var regular = SemanticEngine.AddAlbum(db, "回归相册");
        var timeline = SemanticEngine.AddAlbum(db, "2024年01月", "时间线相册（自动生成）");
        foreach (var image in records)
        {
            SemanticEngine.AddToAlbum(db, regular.Id, image.Id);
            SemanticEngine.AddToAlbum(db, timeline.Id, image.Id);
        }
        db.SetImageLocation(records[0].Id,
            new LocationHierarchyChoice("中国 / 陕西省 / 西安市", "中国", "陕西省", "西安市"));
    }
}
