using Picer.Core.Vision;
using System;
using System.IO;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Picer.E2E;

public sealed class FlorenceRuntimeTests
{
    [Fact]
    public async Task BundledFlorenceInt8_LoadsAllStagesAndRunsVisionEncoderOnCpu()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var directory = Path.Combine(root, "third_party", "runtime", "models", "florence-2-base-ft");
        var result = await new FlorenceRuntimeValidationService(directory).ValidateAsync(FlorenceExecutionProvider.Cpu);
        Assert.True(result.Available, string.Join(Environment.NewLine, result.Stages.Select(x => $"{x.Stage}: {x.Detail}")));
        Assert.True(result.VisionColdMilliseconds > 0);
        Assert.True(result.VisionWarmMilliseconds > 0);
    }

    [Fact]
    public async Task DirectMLProbe_ReturnsCompatibilityForEveryStageWithoutCrashing()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var directory = Path.Combine(root, "third_party", "runtime", "models", "florence-2-base-ft");
        var result = await new FlorenceRuntimeValidationService(directory).ValidateAsync(FlorenceExecutionProvider.DirectML);
        Assert.Equal(4, result.Stages.Count);
    }

    [Fact]
    public async Task FlorencePipeline_GeneratesTokensFromImageOnCpu()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var directory = Path.Combine(root, "third_party", "runtime", "models", "florence-2-base-ft");
        var path = Path.Combine(Path.GetTempPath(), $"florence-{Guid.NewGuid():N}.png");
        try
        {
            using (var image = new Image<Rgb24>(96, 96, new Rgb24(30, 140, 220))) image.SaveAsPng(path);
            using var service = new FlorenceInferenceService(directory, FlorenceExecutionProvider.Cpu);
            var result = await service.RunAsync(path, FlorenceTask.Caption, 8);
            Assert.True(result.GeneratedTokens >= 3);
            Assert.False(string.IsNullOrWhiteSpace(result.Text));
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
