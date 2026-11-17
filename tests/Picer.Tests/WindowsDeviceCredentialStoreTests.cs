using Picer.Core.Identity;

namespace Picer.Tests;

public sealed class WindowsDeviceCredentialStoreTests
{
    [Fact]
    public async Task SaveRetriesTransientDestinationLock()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "picer-credential-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "device.bin");
        try
        {
            var store = new WindowsDeviceCredentialStore(path);
            var connection = new DeviceConnection("https://127.0.0.1:1", Guid.NewGuid().ToString("N"), "pin", "key",
                "Picer.Device." + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "test-device", "gui");
            store.Save(connection);
            using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            var release = Task.Run(async () => { await Task.Delay(130); held.Dispose(); });
            store.Save(connection with { DeviceName = "updated-device" });
            await release;
            Assert.Equal("updated-device", store.Load().DeviceName);
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
