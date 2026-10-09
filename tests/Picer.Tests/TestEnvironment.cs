using System.Runtime.CompilerServices;

internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var root = Environment.GetEnvironmentVariable("PICER_TEST_TEMP");
        if (string.IsNullOrEmpty(root)) root = Path.Combine(Picer.Core.PicerPaths.Resolve("testTemp", "artifacts/test-temp"), "tests");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("TEMP", root);
        Environment.SetEnvironmentVariable("TMP", root);
        Environment.SetEnvironmentVariable("PICER_SCAN_CACHE_ROOT", Path.Combine(root, "scan-manifests-" + Environment.ProcessId));
    }
}
