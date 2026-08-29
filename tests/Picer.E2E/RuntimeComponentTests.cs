using System.IO;
using Picer.App;
using Picer.Core.Video;

namespace Picer.E2E;

public sealed class RuntimeComponentTests
{
    [Fact]
    public void StartupPreflight_ReportsMissingBundledComponentsWithoutHashing()
    {
        var isolatedRoot = Path.Combine(Path.GetTempPath(), $"picer-runtime-preflight-{Guid.NewGuid():N}");
        Directory.CreateDirectory(isolatedRoot);

        var missing = new RuntimeComponentService(isolatedRoot).GetMissingComponentNames();

        Assert.Equal(RuntimeComponentService.RequiredComponents.Count, missing.Count);
        Assert.Contains("FFmpeg", missing);
        Assert.Contains("Chinese-CLIP 中文语义模型", missing);
    }

    [Fact]
    public void FfmpegLocator_DoesNotDependOnDeveloperMachinePath()
    {
        var isolatedBase = Path.Combine(Path.GetTempPath(), $"picer-ffmpeg-locator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(isolatedBase);
        var previous = Environment.GetEnvironmentVariable("PICER_FFMPEG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PICER_FFMPEG_PATH", null);
            Assert.Null(new FfmpegLocator(isolatedBase).Find());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PICER_FFMPEG_PATH", previous);
        }
    }

    [Fact]
    public async Task PublishedRuntimeComponents_ArePresentAndMatchPinnedHashes()
    {
        var service = new RuntimeComponentService(Path.Combine(AppContext.BaseDirectory, "runtime"));

        var result = await service.VerifyAsync();

        Assert.Equal(RuntimeComponentService.RequiredComponents.Count, result.Count);
        Assert.All(result, component => Assert.True(component.Ready, $"{component.Spec.Name}: {component.Detail}"));
    }

    [Fact]
    public async Task PublishedRuntimeComponents_PassOfflineSelfTest()
    {
        var service = new RuntimeComponentService(Path.Combine(AppContext.BaseDirectory, "runtime"));

        var messages = await service.SelfTestAsync();

        Assert.Equal(3, messages.Count);
        Assert.Contains(messages, message => message.StartsWith("人脸模型：", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.StartsWith("FFmpeg：", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.StartsWith("whisper.cpp：", StringComparison.Ordinal));
    }
}
