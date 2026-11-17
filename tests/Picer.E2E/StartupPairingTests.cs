using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using Picer.Core.Identity;

namespace Picer.E2E;

public sealed class StartupPairingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnpairedStartupShowsConnectionWindowAndCancelExitsWithoutCrash(bool hasPendingConnection)
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-startup-pairing-" + Guid.NewGuid().ToString("N"));
        var bootstrap = Path.Combine(root, "bootstrap");
        Directory.CreateDirectory(bootstrap);
        var config = "{\"mode\":\"service\",\"authentication\":\"device\"}";
        File.WriteAllText(Path.Combine(bootstrap, "backend.json"), config);
        var credential = Path.Combine(bootstrap, "account-device.bin");
        if (hasPendingConnection)
            new WindowsDeviceCredentialStore(credential).Save(new DeviceConnection("https://127.0.0.1:1",
                Guid.NewGuid().ToString("N"), "", "", "Picer.Device." + Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"), "test-device", "gui"));
        var before = File.Exists(credential) ? File.ReadAllBytes(credential) : null;
        var app = Path.Combine(Path.GetDirectoryName(typeof(Picer.App.App).Assembly.Location)!, "Picer.App.exe");
        using var process = Process.Start(new ProcessStartInfo(app)
        {
            UseShellExecute = false,
            Environment = { ["PICER_DATA_ROOT"] = root, ["PICER_BOOTSTRAP_ROOT"] = bootstrap }
        })!;
        try
        {
            AutomationElement? window = null;
            Assert.True(SpinWait.SpinUntil(() =>
            {
                if (process.HasExited) return false;
                process.Refresh();
                if (process.MainWindowHandle == IntPtr.Zero) return false;
                window = AutomationElement.FromHandle(process.MainWindowHandle);
                return window?.Current.Name == "登录照片空间";
            }, TimeSpan.FromSeconds(20)), "应显示可恢复的登录窗口，不能只检测到 Picer 错误弹窗");
            var button = window!.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "LoginButton"));
            Assert.NotNull(button);
            Assert.True(button.Current.IsEnabled);
            var cancel = window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, "取消"));
            Assert.NotNull(cancel);
            ((InvokePattern)cancel.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            Assert.True(process.WaitForExit(10000));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(config, File.ReadAllText(Path.Combine(bootstrap, "backend.json")));
            if (before is not null) Assert.Equal(before, File.ReadAllBytes(credential));
            else Assert.False(File.Exists(credential));
            Assert.False(File.Exists(Path.Combine(root, "picer.db")));
            var log = Path.Combine(root, "logs", "crash.log");
            Assert.DoesNotContain("CRASH:", File.ReadAllText(log));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
