using System;
using System.IO;
using Picer.App;

namespace Picer.E2E;

public sealed class RemoteMirrorCleanupTests
{
    [Fact]
    public void SuccessfulLocalSwitchCleanupDoesNotDeleteLocalDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-mirror-cleanup-" + Guid.NewGuid().ToString("N"));
        var bootstrap = Path.Combine(root, "bootstrap");
        var oldData = Environment.GetEnvironmentVariable("PICER_DATA_ROOT");
        var oldBootstrap = Environment.GetEnvironmentVariable("PICER_BOOTSTRAP_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("PICER_DATA_ROOT", root);
            Environment.SetEnvironmentVariable("PICER_BOOTSTRAP_ROOT", bootstrap);
            Directory.CreateDirectory(Path.Combine(root, "remote-cache"));
            File.WriteAllText(Path.Combine(root, "remote-mirror.db"), "remote");
            File.WriteAllText(Path.Combine(root, "remote-cache", "entry"), "remote");
            File.WriteAllText(AppPaths.DbPath, "local");

            AppPaths.ScheduleRemoteMirrorCleanup(root);
            AppPaths.CleanupScheduledRemoteMirror();

            Assert.True(File.Exists(AppPaths.DbPath));
            Assert.False(File.Exists(Path.Combine(root, "remote-mirror.db")));
            Assert.False(Directory.Exists(Path.Combine(root, "remote-cache")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PICER_DATA_ROOT", oldData);
            Environment.SetEnvironmentVariable("PICER_BOOTSTRAP_ROOT", oldBootstrap);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
