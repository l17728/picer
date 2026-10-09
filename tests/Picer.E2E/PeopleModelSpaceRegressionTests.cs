using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using Picer.Core.Data;
using Picer.Core.Vision;

namespace Picer.E2E;

/// <summary>
/// Regression coverage for the supported InsightFace space. SFace records may
/// remain in storage for provenance, but must never appear as an alternate UI
/// space or leak into the displayed InsightFace collection.
/// </summary>
public sealed class PeopleModelSpaceRegressionTests
{
    [Fact]
    public void SwitchingToInsightFace_LoadsItsArtifactsAndOnlyItsFaces()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-model-space-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "model-space-fixture-root.txt"), root);
        var dbPath = Path.Combine(root, "picer.db");
        SeedModelSpaces(dbPath);
        using (var verificationDb = new PicerDatabase(dbPath))
            Assert.Contains(new VisionAnalysisRepository(verificationDb).GetFaceTrainingHistory("insightface-arcface"), item => item.ValidationAccuracy >= .90);
        var appDirectory = Path.GetDirectoryName(typeof(Picer.App.App).Assembly.Location)!;
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(appDirectory, "Picer.App.exe"))
        {
            UseShellExecute = false,
            Environment =
            {
                ["PICER_DATA_ROOT"] = root,
                ["PICER_BOOTSTRAP_ROOT"] = Path.Combine(root, "bootstrap"),
                // Keep this test independent of a running ModelServer. The
                // Insight artifact is seeded in the local index below.
                ["PICER_MODEL_SERVER_URL"] = "http://127.0.0.1:1"
            }
        })!;

        try
        {
            var window = WaitForWindow(process.Id, TimeSpan.FromSeconds(15));
            Assert.NotNull(window);
            var peopleTab = WaitFor(window!, ControlType.TabItem, name => name.Contains("人物", StringComparison.Ordinal), TimeSpan.FromSeconds(8));
            Assert.NotNull(peopleTab);
            ((SelectionItemPattern)peopleTab!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();

            var modelCombo = WaitForElement(window, "人脸模型空间下拉列表", TimeSpan.FromSeconds(8));
            Assert.NotNull(modelCombo);

            // SFace records remain in storage, but only InsightFace is exposed.
            var insightTitle = WaitFor(window, ControlType.Text,
                name => name.Contains("模型空间", StringComparison.OrdinalIgnoreCase)
                        && name.Contains("InsightFace", StringComparison.OrdinalIgnoreCase)
                        && !name.Contains("SFace", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(15));
            Assert.NotNull(insightTitle);

            var pending = WaitForElement(window, "待处理人脸", TimeSpan.FromSeconds(8));
            Assert.NotNull(pending);
            ((InvokePattern)pending!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            var faceList = WaitForElement(window, "人物人脸列表", TimeSpan.FromSeconds(8));
            Assert.NotNull(faceList);
            Assert.Equal(1, WaitForListItems(faceList!, TimeSpan.FromSeconds(15)));
            Assert.Null(ExpandAndFind(modelCombo!, name => name.Contains("SFace", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(2)));
            Assert.NotNull(ExpandAndFind(modelCombo!, name => name.Contains("InsightFace", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(5)));
            Key(0x1B); // close model selector popup

            // The seeded InsightFace artifact must populate the model-scoped
            // history list; an empty list is the production bug this test guards.
            var historyCombo = WaitForElement(window, "模型历史制品下拉列表", TimeSpan.FromSeconds(15));
            Assert.NotNull(historyCombo);
            var historyItem = FindPopupItemContaining(historyCombo!, "93", TimeSpan.FromSeconds(15));
            Assert.NotNull(historyItem);

            Assert.Equal(1, CountListItems(faceList!));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(3000)) process.Kill(true);
            }
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void SeedModelSpaces(string path)
    {
        using var db = new PicerDatabase(path);
        var source = db.AddSource(Path.Combine(Path.GetDirectoryName(path)!, "photos"));
        db.Command("DROP TRIGGER IF EXISTS images_ai").ExecuteNonQuery();
        var imageIds = new List<long>();
        for (var i = 0; i < 2; i++)
        {
            using var image = db.Command("""
                INSERT INTO images(source_id,path,file_size,modified_utc,sha256,width,height,status,indexed_utc,ahash,dhash)
                VALUES($source,$path,100,$utc,NULL,1920,1080,'active',$utc,0,0);
                SELECT last_insert_rowid();
                """);
            image.Parameters.AddWithValue("$source", source.Id);
            image.Parameters.AddWithValue("$path", Path.Combine(source.Path, $"model-space-{i}.jpg"));
            image.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            imageIds.Add((long)image.ExecuteScalar()!);
        }

        // SFace has two records, InsightFace only one. Both are complete
        // vectors, so the test does not exercise a slow backfill path.
        InsertFace(db, imageIds[0], "sface", 1);
        InsertFace(db, imageIds[1], "sface", 2);
        InsertFace(db, imageIds[0], "insightface-arcface", 3);

        var metrics = new
        {
            metrics = new
            {
                totalSamples = 10, totalPersons = 2, trainAccuracy = .95,
                validationAccuracy = .93, falseAcceptRate = .01,
                falseRejectRate = .02, meanSimilaritySame = .9, meanSimilarityDiff = .2
            }
        };
        using var artifact = db.Command("""
            INSERT INTO face_training_artifacts
              (artifact_id,dataset_id,endpoint,model_version,artifact_json,false_accept_rate,false_reject_rate,state,created_utc,applied_utc)
            VALUES($id,'e2e','local','insightface-arcface',$json,.01,.02,'active',$created,$created)
            """);
        artifact.Parameters.AddWithValue("$id", "insight-artifact-93");
        artifact.Parameters.AddWithValue("$json", JsonSerializer.Serialize(metrics));
        artifact.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
        artifact.ExecuteNonQuery();
    }

    private static void InsertFace(PicerDatabase db, long imageId, string model, long marker)
    {
        using var face = db.Command("""
            INSERT INTO detected_faces(image_id,model_version,ordinal,box_x,box_y,box_width,box_height,confidence,embedding,created_utc)
            VALUES($image,$model,0,.1,.1,.5,.5,.99,$embedding,$created)
            """);
        face.Parameters.AddWithValue("$image", imageId);
        face.Parameters.AddWithValue("$model", model);
        face.Parameters.AddWithValue("$embedding", new byte[512]);
        face.Parameters.AddWithValue("$created", DateTime.UtcNow.AddSeconds(marker).ToString("O"));
        face.ExecuteNonQuery();
    }

    private static AutomationElement? WaitForWindow(int processId, TimeSpan timeout)
        => WaitUntil(() => AutomationElement.RootElement.FindFirst(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId)), timeout);

    private static AutomationElement? WaitForElement(AutomationElement root, string name, TimeSpan timeout)
        => WaitUntil(() => root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, name)), timeout);

    private static AutomationElement? WaitFor(AutomationElement root, ControlType type,
        Func<string, bool> predicate, TimeSpan timeout)
        => WaitUntil(() => root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, type)).Cast<AutomationElement>()
            .FirstOrDefault(element => predicate(element.Current.Name)), timeout);

    private static AutomationElement? ExpandAndFind(AutomationElement combo, Func<string, bool> predicate, TimeSpan timeout)
    {
        if (combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern))
            ((ExpandCollapsePattern)pattern).Expand();
        return WaitUntil(() => AutomationElement.RootElement.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Cast<AutomationElement>()
            .FirstOrDefault(element => predicate(element.Current.Name)), timeout);
    }

    private static IReadOnlyList<AutomationElement> ExpandItems(AutomationElement combo, TimeSpan timeout)
    {
        if (combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern))
            ((ExpandCollapsePattern)pattern).Expand();
        return WaitUntil(() => AutomationElement.RootElement.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Cast<AutomationElement>()
            .Where(item => item.Current.IsEnabled).ToArray(), timeout) ?? [];
    }

    private static AutomationElement? FindPopupItemContaining(AutomationElement combo, string text, TimeSpan timeout)
    {
        combo.SetFocus();
        Key(0x73); // F4: reliably opens WPF ComboBox popup
        return WaitUntil(() => AutomationElement.RootElement.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.IsEnabledProperty, true)).Cast<AutomationElement>()
            .FirstOrDefault(item => item.Current.Name.Contains(text, StringComparison.OrdinalIgnoreCase)), timeout);
    }

    private static void Activate(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
        {
            ((SelectionItemPattern)selection).Select();
            return;
        }
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
            return;
        }
        var patterns = string.Join(",", element.GetSupportedPatterns().Select(pattern => pattern.ProgrammaticName));
        throw new InvalidOperationException($"UI 元素不支持选择或点击：{element.Current.Name}; patterns={patterns}");
    }

    private static void SelectModelOption(AutomationElement window, AutomationElement combo, string displayName)
    {
        SetForegroundWindow(new IntPtr(window.Current.NativeWindowHandle));
        // SetFocus alone is not reliable when another WPF popup owned focus;
        // click the actual ComboBox first, then use keyboard navigation.
        ClickElement(window, combo);
        combo.SetFocus();
        if (combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandPattern) &&
            expandPattern is ExpandCollapsePattern expander &&
            expander.Current.ExpandCollapseState != ExpandCollapseState.Expanded)
            expander.Expand();
        // WPF's popup item may expose neither SelectionItemPattern nor a
        // usable bounding rectangle.  The fixture has exactly two options;
        // The fixture exposes InsightFace as the first option; HOME selects
        // it without depending on popup UIA implementation details.
        if (displayName.Contains("InsightFace", StringComparison.OrdinalIgnoreCase))
        {
            Key(0x24); // HOME: deterministic first option
        }
        Key(0x0D); // ENTER
    }

    private static void ClickElement(AutomationElement window, AutomationElement element)
    {
        SetForegroundWindow(new IntPtr(window.Current.NativeWindowHandle));
        var bounds = element.Current.BoundingRectangle;
        if (bounds.IsEmpty)
            throw new InvalidOperationException($"模型选项没有可点击区域：{element.Current.Name}");
        SetCursorPos((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(120);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    private static void Key(byte virtualKey)
    {
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, 2, UIntPtr.Zero);
        Thread.Sleep(80);
    }

    private static int WaitForListItems(AutomationElement list, TimeSpan timeout)
        => WaitUntil(() => CountListItems(list) > 0 ? CountListItems(list) : 0, timeout);

    private static bool WaitForZero(AutomationElement list, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (CountListItems(list) == 0) return true;
            Thread.Sleep(100);
        }
        return CountListItems(list) == 0;
    }

    private static int CountListItems(AutomationElement list)
        => list.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Count;

    private static T? WaitUntil<T>(Func<T?> probe, TimeSpan timeout) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        T? value;
        do
        {
            value = probe();
            if (value is not null) return value;
            Thread.Sleep(100);
        } while (DateTime.UtcNow < deadline);
        return value;
    }

    private static int WaitUntil(Func<int> probe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var value = 0;
        do
        {
            value = probe();
            if (value > 0) return value;
            Thread.Sleep(100);
        } while (DateTime.UtcNow < deadline);
        return value;
    }
}
