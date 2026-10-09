using System.IO;
using Picer.App;
using Picer.MediaAgent;

namespace Picer.E2E;

public sealed class MediaAgentControllerTests
{
    [Fact]
    public void ResolveExecutable_FindsMediaAgentFromPortableGuiDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-agent-path-" + Guid.NewGuid().ToString("N"));
        var appBase = Path.Combine(root, "artifacts", "build-portable", "Debug", "net8.0-windows");
        var executable = Path.Combine(root, "src", "Picer.MediaAgent", "bin", "Debug", "net8.0-windows", "Picer.MediaAgent.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllBytes(executable, [0x4D, 0x5A]);
        File.WriteAllText(Path.ChangeExtension(executable, ".dll"), "fixture");
        File.WriteAllText(Path.ChangeExtension(executable, ".runtimeconfig.json"), "{}");
        File.WriteAllText(Path.ChangeExtension(executable, ".deps.json"), "{}");

        try
        {
            Assert.Equal(Path.GetFullPath(executable), MediaAgentController.ResolveExecutableForBaseDirectory(appBase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveExecutable_SkipsOrphanAppHostBesideGui()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-agent-orphan-" + Guid.NewGuid().ToString("N"));
        var appBase = Path.Combine(root, "src", "Picer.App", "bin", "Debug", "net8.0-windows");
        var valid = Path.Combine(root, "src", "Picer.MediaAgent", "bin", "Debug", "net8.0-windows", "Picer.MediaAgent.exe");
        Directory.CreateDirectory(appBase);
        Directory.CreateDirectory(Path.GetDirectoryName(valid)!);
        File.WriteAllBytes(Path.Combine(appBase, "Picer.MediaAgent.exe"), [0x4D, 0x5A]);
        File.WriteAllBytes(valid, [0x4D, 0x5A]);
        File.WriteAllText(Path.ChangeExtension(valid, ".dll"), "fixture");
        File.WriteAllText(Path.ChangeExtension(valid, ".runtimeconfig.json"), "{}");
        File.WriteAllText(Path.ChangeExtension(valid, ".deps.json"), "{}");
        try
        {
            Assert.Equal(valid, MediaAgentController.ResolveExecutableForBaseDirectory(appBase));
            File.Delete(Path.ChangeExtension(valid, ".dll"));
            // Test temp can live under the real repository, whose complete
            // agent remains a legitimate ancestor fallback.
            Assert.NotEqual(valid, MediaAgentController.ResolveExecutableForBaseDirectory(appBase));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ModelEndpointResolver_UsesServiceConfigAndRejectsNonLoopbackTargets()
    {
        Assert.Equal(ModelEndpointResolver.DefaultEndpoint,
            ModelEndpointResolver.Resolve(null).ToString());
        Assert.Equal("http://127.0.0.1:8123/v1/chat",
            ModelEndpointResolver.Resolve("http://127.0.0.1:8123/v1/chat").ToString());
        Assert.Throws<InvalidOperationException>(() =>
            ModelEndpointResolver.Resolve("https://example.invalid/v1/chat"));
    }
}
