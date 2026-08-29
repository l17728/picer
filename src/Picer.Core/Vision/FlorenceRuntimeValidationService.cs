using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Picer.Core.Vision;

public enum FlorenceExecutionProvider { Cpu, DirectML }
public sealed record FlorenceStageResult(string Stage, bool Loaded, double LoadMilliseconds, string Detail);
public sealed record FlorenceValidationResult(FlorenceExecutionProvider Provider, bool Available, double VisionColdMilliseconds,
    double VisionWarmMilliseconds, long WorkingSetDeltaBytes, IReadOnlyList<FlorenceStageResult> Stages, string RecommendationReason);

public sealed class FlorenceRuntimeValidationService(string modelDirectory)
{
    private static readonly (string Stage, string File)[] Models =
    [
        ("视觉编码器", "vision_encoder_quantized.onnx"), ("文本编码器", "encoder_model_quantized.onnx"),
        ("词嵌入", "embed_tokens_quantized.onnx"), ("自回归解码器", "decoder_model_quantized.onnx")
    ];
    public string ModelDirectory { get; } = Path.GetFullPath(modelDirectory);

    public async Task<FlorenceValidationResult> ValidateAsync(FlorenceExecutionProvider provider,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        await Task.Run(() => Validate(provider, progress, cancellationToken), cancellationToken).ConfigureAwait(false);

    private FlorenceValidationResult Validate(FlorenceExecutionProvider provider, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var stages = new List<FlorenceStageResult>(); var before = Process.GetCurrentProcess().WorkingSet64;
        var cold = 0d; var warm = 0d;
        foreach (var model in Models)
        {
            cancellationToken.ThrowIfCancellationRequested(); progress?.Report($"{provider}：加载{model.Stage}……");
            var path = Path.Combine(ModelDirectory, model.File);
            if (!File.Exists(path)) { stages.Add(new(model.Stage, false, 0, "模型文件缺失")); continue; }
            var watch = Stopwatch.StartNew();
            try
            {
                using var options = CreateOptions(provider);
                using var session = new InferenceSession(path, options);
                var load = watch.Elapsed.TotalMilliseconds;
                if (model.File.StartsWith("vision_encoder", StringComparison.Ordinal))
                {
                    var input = new DenseTensor<float>(new float[3 * 768 * 768], [1, 3, 768, 768]);
                    watch.Restart(); using (session.Run([NamedOnnxValue.CreateFromTensor("pixel_values", input)], ["image_features"])) { } cold = watch.Elapsed.TotalMilliseconds;
                    cancellationToken.ThrowIfCancellationRequested(); watch.Restart(); using (session.Run([NamedOnnxValue.CreateFromTensor("pixel_values", input)], ["image_features"])) { } warm = watch.Elapsed.TotalMilliseconds;
                    stages.Add(new(model.Stage, true, load, $"实际推理成功；输出 [1,577,768]"));
                }
                else stages.Add(new(model.Stage, true, load, "ONNX 加载和图优化成功"));
            }
            catch (Exception ex) { stages.Add(new(model.Stage, false, watch.Elapsed.TotalMilliseconds, ex.Message)); }
        }
        var available = stages.Count == Models.Length && stages.All(x => x.Loaded);
        var delta = Math.Max(0, Process.GetCurrentProcess().WorkingSet64 - before);
        var reason = !available ? $"{provider} 至少一个子模型不兼容，不会自动启用。" : $"{provider} 四个子模型兼容，视觉编码热态 {warm:F0} ms。";
        return new(provider, available, cold, warm, delta, stages, reason);
    }

    private static SessionOptions CreateOptions(FlorenceExecutionProvider provider)
    {
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        if (provider == FlorenceExecutionProvider.DirectML)
        {
            options.EnableMemoryPattern = false; options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            options.AppendExecutionProvider_DML(0);
        }
        return options;
    }
}
