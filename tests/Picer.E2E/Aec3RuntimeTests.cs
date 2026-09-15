using System.IO;
using Picer.App.Services;
using Xunit.Abstractions;

namespace Picer.E2E;

public sealed class Aec3RuntimeTests
{
    private readonly ITestOutputHelper _output;

    public Aec3RuntimeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void MissingAec3Runtime_FallsBackWithoutThrowing()
    {
        var previous = Environment.GetEnvironmentVariable("PICER_AEC3_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PICER_AEC3_PATH",
                Path.Combine(Path.GetTempPath(), $"missing-aec3-{Guid.NewGuid():N}.dll"));

            Assert.False(WebRtcAec3Processor.TryCreate(16000, out var processor, out var detail));
            Assert.Null(processor);
            Assert.NotEmpty(detail);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PICER_AEC3_PATH", previous);
        }
    }

    [Fact]
    [Trait("Category", "RuntimeComponent")]
    public void Aec3_SuppressesSyntheticDelayedEcho()
    {
        Assert.True(WebRtcAec3Processor.TryCreate(16000, out var processor, out var detail), detail);
        using (processor)
        {
            Assert.NotNull(processor);
            Assert.Contains("aec3", processor.Version, StringComparison.OrdinalIgnoreCase);

            const int delayFrames = 8;
            const int totalFrames = 700;
            var renderHistory = new Queue<short[]>();
            double inputEnergy = 0;
            double outputEnergy = 0;
            var measuredSamples = 0;
            var phase = 0d;

            for (var frameIndex = 0; frameIndex < totalFrames; frameIndex++)
            {
                var render = CreateRenderFrame(processor.FrameSamples, ref phase, frameIndex);
                renderHistory.Enqueue(render);
                var renderBytes = ToBytes(render);
                Assert.True(processor.FeedRender(renderBytes));

                var delayed = renderHistory.Count > delayFrames ? renderHistory.Dequeue() : new short[processor.FrameSamples];
                var capture = delayed.Select(sample => (short)(sample * 0.55)).ToArray();
                var captureBytes = ToBytes(capture);
                if (frameIndex >= 400)
                    inputEnergy += capture.Sum(sample => (double)sample * sample);

                Assert.True(processor.ProcessCapture(captureBytes, delayFrames * 10));
                if (frameIndex >= 400)
                {
                    var cleaned = new short[capture.Length];
                    Buffer.BlockCopy(captureBytes, 0, cleaned, 0, captureBytes.Length);
                    outputEnergy += cleaned.Sum(sample => (double)sample * sample);
                    measuredSamples += cleaned.Length;
                }
            }

            Assert.True(measuredSamples > 0);
            var attenuation = outputEnergy / Math.Max(1, inputEnergy);
            _output.WriteLine($"AEC3 synthetic echo energy ratio: {attenuation:F6}");
            Assert.True(attenuation < 0.35, $"Expected echo attenuation below 0.35, actual={attenuation:F4}");
        }
    }

    private static short[] CreateRenderFrame(int samples, ref double phase, int frameIndex)
    {
        var result = new short[samples];
        var amplitude = frameIndex % 70 < 52 ? 0.32 : 0.08;
        for (var index = 0; index < samples; index++)
        {
            var value = amplitude * (Math.Sin(phase) + 0.45 * Math.Sin(phase * 1.91));
            result[index] = (short)Math.Clamp(value * short.MaxValue, short.MinValue, short.MaxValue);
            phase += 2 * Math.PI * 237 / 16000;
        }
        return result;
    }

    private static byte[] ToBytes(short[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
