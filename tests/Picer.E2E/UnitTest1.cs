using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using Picer.App.FileOperations;
using Picer.Core.FileOperations;
using Picer.Core.Hashing;
using Picer.Core.Data;

namespace Picer.E2E;

public sealed class DesktopSmokeTests
{
    [Fact]
    public void AllSafeFeatureWindows_InitializeWithRealApplicationServices()
    {
        var isolatedData = Path.Combine(Path.GetTempPath(), "picer-ui-dialogs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolatedData);
        SeedLibrary(Path.Combine(isolatedData, "picer.db"), 1);
        var previousDataRoot = Environment.GetEnvironmentVariable("PICER_DATA_ROOT");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Environment.SetEnvironmentVariable("PICER_DATA_ROOT", isolatedData);
                Picer.App.AppServices.Initialize();
                var people = new Picer.App.ViewModels.PeopleViewModel(Picer.App.AppServices.Db, Picer.App.AppServices.People,
                    Picer.App.AppServices.VisionRepository, Picer.App.AppServices.VisionAnalysis, Picer.App.AppServices.FaceThumbnails,
                    Picer.App.AppServices.FaceSuggestions, Picer.App.AppServices.UnknownFaceClusters, Picer.App.AppServices.VisionUnavailableReason);
                using var mainViewModel = new Picer.App.ViewModels.MainViewModel(Picer.App.AppServices.Db,
                    Picer.App.AppServices.Index, Picer.App.AppServices.Geocoder, people);
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
        var startup = Stopwatch.StartNew();
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            Environment = { ["PICER_DATA_ROOT"] = isolatedData }
        })!;
        try
        {
            var window = WaitForWindow(process.Id, TimeSpan.FromSeconds(15));
            Assert.NotNull(window);
            // Covers cold .NET/WPF process activation; data loading starts after the window is visible.
            Assert.True(startup.Elapsed < TimeSpan.FromSeconds(4), $"首窗耗时 {startup.Elapsed}");
            Assert.NotNull(window!.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, "搜索图片")));
            Assert.NotNull(window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, "图片网格")));
            Assert.NotNull(window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, "添加文件夹")));
            foreach (var entry in new[] { "重建索引", "检查缺失", "查找重复", "删除所选" })
            {
                Assert.True(window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, entry)) is not null,
                    $"主窗口缺少可访问功能入口：{entry}");
            }
            AssertMenuEntries(window, "文件菜单", "添加文件夹", "存储设置", "备份与恢复");
            AssertMenuEntries(window, "AI 功能菜单", "云端分析设置", "描述选中照片", "任务中心",
                "中文语义检索", "Florence 运行验证", "本地描述任务");
            AssertMenuEntries(window, "工具菜单", "查找重复", "运行组件", "诊断中心", "扫描视频");
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
            Assert.InRange(items.Count, 1, 100);
            if (grid.TryGetCurrentPattern(ScrollPattern.Pattern, out var scrollObject) && scrollObject is ScrollPattern scroll)
            {
                for (var i = 0; i < 20; i++) scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
                Thread.Sleep(300);
                Assert.InRange(grid.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Count, 1, 100);
            }
            process.Refresh();
            Assert.True(process.WorkingSet64 < 300L * 1024 * 1024, $"滚动后工作集 {process.WorkingSet64 / 1024 / 1024} MB");
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
        ((ExpandCollapsePattern)pattern).Expand();
        Thread.Sleep(100);
        foreach (var entry in entries)
            Assert.NotNull(AutomationElement.RootElement.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, entry)));
        ((ExpandCollapsePattern)pattern).Collapse();
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
}
