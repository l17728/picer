using System.Runtime.CompilerServices;
using System.IO;

internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var root = Environment.GetEnvironmentVariable("PICER_TEST_TEMP");
        if (string.IsNullOrEmpty(root)) root = Path.Combine(Picer.Core.PicerPaths.Resolve("testTemp", "artifacts/test-temp"), "e2e");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("TEMP", root);
        Environment.SetEnvironmentVariable("TMP", root);
    }
}
