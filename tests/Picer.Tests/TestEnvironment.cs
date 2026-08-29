using System.Runtime.CompilerServices;

internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var root = Environment.GetEnvironmentVariable("PICER_TEST_TEMP") ?? @"D:\PicerData\temp\tests";
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("TEMP", root);
        Environment.SetEnvironmentVariable("TMP", root);
    }
}
