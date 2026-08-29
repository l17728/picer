using System.Runtime.CompilerServices;
using System.IO;

internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var root = Environment.GetEnvironmentVariable("PICER_TEST_TEMP") ?? @"D:\PicerData\temp\e2e";
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("TEMP", root);
        Environment.SetEnvironmentVariable("TMP", root);
    }
}
