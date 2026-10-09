using Picer.Core.Imaging;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Picer.Core.Vision;

public enum FlorenceTask { Caption, DetailedCaption, Ocr }
public sealed record FlorenceInferenceResult(string Text, FlorenceTask Task, FlorenceExecutionProvider Provider, int GeneratedTokens, TimeSpan Elapsed);

public sealed class FlorenceInferenceService : IDisposable
{
    private readonly InferenceSession _vision, _embedding, _encoder, _decoder;
    private readonly FlorenceBpeTokenizer _tokenizer;
    public FlorenceExecutionProvider Provider { get; }

    public FlorenceInferenceService(string modelDirectory, FlorenceExecutionProvider provider)
    {
        Provider = provider; var options = CreateOptions(provider);
        try
        {
            _vision = new(Path.Combine(modelDirectory, "vision_encoder_quantized.onnx"), options);
            _embedding = new(Path.Combine(modelDirectory, "embed_tokens_quantized.onnx"), options);
            _encoder = new(Path.Combine(modelDirectory, "encoder_model_quantized.onnx"), options);
            _decoder = new(Path.Combine(modelDirectory, "decoder_model_quantized.onnx"), options);
        }
        finally { options.Dispose(); }
        _tokenizer = new FlorenceBpeTokenizer(Path.Combine(modelDirectory, "vocab.json"), Path.Combine(modelDirectory, "merges.txt"), Path.Combine(modelDirectory, "added_tokens.json"));
    }

    public async Task<FlorenceInferenceResult> RunAsync(string imagePath, FlorenceTask task, int maximumTokens = 96,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        await Task.Run(() => Run(imagePath, task, Math.Clamp(maximumTokens, 8, 256), progress, cancellationToken), cancellationToken).ConfigureAwait(false);

    private FlorenceInferenceResult Run(string imagePath, FlorenceTask task, int maximumTokens, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow; cancellationToken.ThrowIfCancellationRequested(); progress?.Report("正在预处理 768×768 图像……");
        var pixels = Preprocess(imagePath);
        progress?.Report("正在运行视觉编码器……");
        float[] imageFeatures;
        using (var output = _vision.Run([NamedOnnxValue.CreateFromTensor("pixel_values", new DenseTensor<float>(pixels, [1, 3, 768, 768]))], ["image_features"])) imageFeatures = output.First().AsEnumerable<float>().ToArray();

        cancellationToken.ThrowIfCancellationRequested(); var prompt = task switch { FlorenceTask.Caption => "What does the image describe?", FlorenceTask.DetailedCaption => "Describe in detail what is shown in the image.", _ => "What is the text in the image?" };
        var promptIds = _tokenizer.Encode(prompt);
        float[] promptEmbeddings;
        using (var output = _embedding.Run([NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(promptIds, [1, promptIds.Length]))], ["inputs_embeds"])) promptEmbeddings = output.First().AsEnumerable<float>().ToArray();
        var imageLength = imageFeatures.Length / 768; var totalLength = imageLength + promptIds.Length;
        var combined = new float[(imageFeatures.Length + promptEmbeddings.Length)]; imageFeatures.CopyTo(combined, 0); promptEmbeddings.CopyTo(combined, imageFeatures.Length);
        var attention = Enumerable.Repeat(1L, totalLength).ToArray(); progress?.Report("正在运行多模态文本编码器……");
        float[] hidden;
        using (var output = _encoder.Run([
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attention, [1, totalLength])),
            NamedOnnxValue.CreateFromTensor("inputs_embeds", new DenseTensor<float>(combined, [1, totalLength, 768]))], ["last_hidden_state"])) hidden = output.First().AsEnumerable<float>().ToArray();

        var generated = new List<long> { 2 };
        for (var step = 0; step < maximumTokens; step++)
        {
            cancellationToken.ThrowIfCancellationRequested(); progress?.Report($"正在离线生成文本 {step + 1}/{maximumTokens}……");
            float[] tokenEmbedding;
            using (var output = _embedding.Run([NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(generated.ToArray(), [1, generated.Count]))], ["inputs_embeds"])) tokenEmbedding = output.First().AsEnumerable<float>().ToArray();
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("encoder_attention_mask", new DenseTensor<long>(attention, [1,totalLength])),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", new DenseTensor<float>(hidden, [1,totalLength,768])),
                NamedOnnxValue.CreateFromTensor("inputs_embeds", new DenseTensor<float>(tokenEmbedding, [1,generated.Count,768]))
            };
            using var outputs = _decoder.Run(inputs);
            var logits = outputs.First(x => x.Name == "logits").AsEnumerable<float>().ToArray();
            var next = step == 0 ? 0 : ArgMax(logits.AsSpan(logits.Length - 51289));
            generated.Add(next); if (next == 2 && step > 0) break;
        }
        return new(_tokenizer.Decode(generated), task, Provider, generated.Count, DateTime.UtcNow - started);
    }

    private static long ArgMax(ReadOnlySpan<float> values) { var index = 0; for (var i = 1; i < values.Length; i++) if (values[i] > values[index]) index = i; return index; }
    private static float[] Preprocess(string path)
    {
        using var image = RasterImage.Load(path); image.Resize(768, 768);
        var result = new float[3 * 768 * 768]; float[] mean = [.485f, .456f, .406f], std = [.229f, .224f, .225f];
        var rgb = image.CopyRgbPixels();
        for (var i = 0; i < 768 * 768; i++)
        {
            result[i] = (rgb[i * 3] / 255f - mean[0]) / std[0];
            result[768 * 768 + i] = (rgb[i * 3 + 1] / 255f - mean[1]) / std[1];
            result[2 * 768 * 768 + i] = (rgb[i * 3 + 2] / 255f - mean[2]) / std[2];
        }
        return result;
    }
    private static SessionOptions CreateOptions(FlorenceExecutionProvider provider) { var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL }; if (provider == FlorenceExecutionProvider.DirectML) { options.EnableMemoryPattern = false; options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL; options.AppendExecutionProvider_DML(0); } return options; }
    public void Dispose() { _decoder.Dispose(); _encoder.Dispose(); _embedding.Dispose(); _vision.Dispose(); }
}

internal sealed class FlorenceBpeTokenizer
{
    private readonly Dictionary<string, long> _vocab; private readonly Dictionary<long, string> _tokens; private readonly Dictionary<(string, string), int> _ranks;
    private readonly Dictionary<byte, char> _byteEncoder; private readonly Dictionary<char, byte> _byteDecoder;
    private static readonly Regex Pattern = new("'s|'t|'re|'ve|'m|'ll|'d| ?\\p{L}+| ?\\p{N}+| ?[^\\s\\p{L}\\p{N}]+|\\s+(?!\\S)|\\s+", RegexOptions.Compiled);
    public FlorenceBpeTokenizer(string vocabPath, string mergesPath, string addedTokensPath)
    {
        _vocab = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(vocabPath))!;
        var added = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(addedTokensPath))!; foreach (var x in added) _vocab[x.Key] = x.Value;
        _tokens = _vocab.ToDictionary(x => x.Value, x => x.Key); _ranks = File.ReadLines(mergesPath).Skip(1).Select((x, i) => (Parts: x.Split(' ', 2), i)).Where(x => x.Parts.Length == 2).ToDictionary(x => (x.Parts[0], x.Parts[1]), x => x.i);
        _byteEncoder = BuildByteEncoder(); _byteDecoder = _byteEncoder.ToDictionary(x => x.Value, x => x.Key);
    }
    public long[] Encode(string text)
    {
        var ids = new List<long> { 0 };
        foreach (Match match in Pattern.Matches(text))
        {
            var encoded = string.Concat(Encoding.UTF8.GetBytes(match.Value).Select(x => _byteEncoder[x]));
            foreach (var token in Bpe(encoded)) ids.Add(_vocab.GetValueOrDefault(token, 3));
        }
        ids.Add(2); return ids.ToArray();
    }
    public string Decode(IEnumerable<long> ids)
    {
        var bytes = new List<byte>(); var special = new StringBuilder();
        foreach (var id in ids.Where(x => x is not 0 and not 1 and not 2))
        {
            if (!_tokens.TryGetValue(id, out var token)) continue;
            if (token.StartsWith('<') && token.EndsWith('>')) { special.Append(token); continue; }
            foreach (var character in token) if (_byteDecoder.TryGetValue(character, out var value)) bytes.Add(value);
        }
        return Encoding.UTF8.GetString(bytes.ToArray()).Trim() + special;
    }
    private IEnumerable<string> Bpe(string token)
    {
        var parts = token.Select(x => x.ToString()).ToList();
        while (parts.Count > 1)
        {
            var bestIndex = -1; var bestRank = int.MaxValue;
            for (var i = 0; i < parts.Count - 1; i++) if (_ranks.TryGetValue((parts[i], parts[i + 1]), out var rank) && rank < bestRank) { bestRank = rank; bestIndex = i; }
            if (bestIndex < 0) break; parts[bestIndex] += parts[bestIndex + 1]; parts.RemoveAt(bestIndex + 1);
        }
        return parts;
    }
    private static Dictionary<byte, char> BuildByteEncoder()
    {
        var bytes = Enumerable.Range(33, 94).Concat(Enumerable.Range(161, 12)).Concat(Enumerable.Range(174, 82)).ToList(); var chars = bytes.ToList(); var extra = 0;
        for (var value = 0; value < 256; value++) if (!bytes.Contains(value)) { bytes.Add(value); chars.Add(256 + extra++); }
        return bytes.Select((x, i) => (Byte: (byte)x, Character: (char)chars[i])).ToDictionary(x => x.Byte, x => x.Character);
    }
}
