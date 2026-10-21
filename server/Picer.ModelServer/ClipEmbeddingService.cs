using System.Text;
using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Picer.ModelServer;

public sealed record ClipEmbeddingRequest(string? Text, string? ImageBase64);

public sealed class ClipEmbeddingService : IDisposable
{
    public const string Version = "OFA-Chinese-CLIP-ViT-B-16/Xenova-f269048-int8/magick-catrom-v1";
    private readonly InferenceSession _session;
    private readonly Dictionary<string, long> _vocabulary;
    private readonly SemaphoreSlim _inference;

    public ClipEmbeddingService()
    {
        var concurrency = int.TryParse(Environment.GetEnvironmentVariable("PICER_CLIP_CONCURRENCY"), out var configured)
            ? Math.Clamp(configured, 1, 8)
            : Math.Clamp(Environment.ProcessorCount / 4, 1, 2);
        _inference = new SemaphoreSlim(concurrency, concurrency);
        var root = Environment.GetEnvironmentVariable("PICER_CLIP_MODEL_ROOT");
        var model = Environment.GetEnvironmentVariable("PICER_CLIP_MODEL_PATH")
            ?? Path.Combine(root ?? AppContext.BaseDirectory, "models", "chinese-clip", "model_quantized.onnx");
        var vocabulary = Environment.GetEnvironmentVariable("PICER_CLIP_VOCAB_PATH")
            ?? Path.Combine(root ?? AppContext.BaseDirectory, "models", "chinese-clip", "vocab.txt");
        if (!File.Exists(model) || !File.Exists(vocabulary))
            throw new FileNotFoundException("Chinese-CLIP model assets are not configured on ModelServer.");
        _session = new InferenceSession(model, new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        });
        _vocabulary = File.ReadLines(vocabulary).Select((token, id) => (token, id))
            .ToDictionary(item => item.token, item => (long)item.id, StringComparer.Ordinal);
    }

    public async Task<float[]> EncodeAsync(ClipEmbeddingRequest request, CancellationToken cancellationToken)
    {
        var text = request.Text?.Trim();
        var image = request.ImageBase64;
        if (string.IsNullOrEmpty(text) == string.IsNullOrEmpty(image))
            throw new InvalidDataException("exactly_one_clip_input_required");
        await _inference.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return string.IsNullOrEmpty(text) ? EncodeImage(Convert.FromBase64String(image!)) : EncodeText(text);
        }
        finally { _inference.Release(); }
    }

    private float[] EncodeText(string text)
    {
        var ids = Tokenize(text, 52);
        var input = new DenseTensor<long>(ids, [1, ids.Length]);
        var mask = new DenseTensor<long>(Enumerable.Repeat(1L, ids.Length).ToArray(), [1, ids.Length]);
        var pixels = new DenseTensor<float>(new float[3 * 224 * 224], [1, 3, 224, 224]);
        using var output = _session.Run([
            NamedOnnxValue.CreateFromTensor("input_ids", input),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
            NamedOnnxValue.CreateFromTensor("pixel_values", pixels)], ["text_embeds"]);
        return Normalize(output.First().AsEnumerable<float>().ToArray());
    }

    private float[] EncodeImage(byte[] bytes)
    {
        if (bytes.Length is 0 or > 64 * 1024 * 1024) throw new InvalidDataException("clip_image_size_invalid");
        using var image = new MagickImage(bytes);
        image.AutoOrient();
        image.ColorSpace = ColorSpace.sRGB;
        image.Resize(new MagickGeometry(224, 224) { IgnoreAspectRatio = true, FillArea = true });
        image.Depth = 8;
        var pixelCollection = image.GetPixels()
            ?? throw new InvalidDataException("clip_image_pixels_unavailable");
        var pixels = pixelCollection.ToByteArray(PixelMapping.RGB)
            ?? throw new InvalidDataException("clip_image_pixels_unavailable");
        if (pixels.Length != 224 * 224 * 3) throw new InvalidDataException("clip_image_pixels_invalid");
        var values = new float[3 * 224 * 224];
        var mean = new[] { .48145466f, .4578275f, .40821073f };
        var std = new[] { .26862954f, .26130258f, .27577711f };
        for (var offset = 0; offset < 224 * 224; offset++)
        {
            values[offset] = (pixels[offset * 3] / 255f - mean[0]) / std[0];
            values[224 * 224 + offset] = (pixels[offset * 3 + 1] / 255f - mean[1]) / std[1];
            values[2 * 224 * 224 + offset] = (pixels[offset * 3 + 2] / 255f - mean[2]) / std[2];
        }
        var ids = new DenseTensor<long>(new long[] { 101, 102 }, [1, 2]);
        var mask = new DenseTensor<long>(new long[] { 1, 1 }, [1, 2]);
        var input = new DenseTensor<float>(values, [1, 3, 224, 224]);
        using var output = _session.Run([
            NamedOnnxValue.CreateFromTensor("input_ids", ids),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
            NamedOnnxValue.CreateFromTensor("pixel_values", input)], ["image_embeds"]);
        return Normalize(output.First().AsEnumerable<float>().ToArray());
    }

    private long[] Tokenize(string text, int maximumLength)
    {
        var pieces = new List<long> { Id("[CLS]", 101) };
        foreach (var unit in BasicTokens(text.ToLowerInvariant()))
        {
            if (_vocabulary.TryGetValue(unit, out var exact)) { pieces.Add(exact); continue; }
            var start = 0;
            while (start < unit.Length)
            {
                var end = unit.Length; long found = -1;
                while (end > start)
                {
                    var candidate = (start == 0 ? "" : "##") + unit[start..end];
                    if (_vocabulary.TryGetValue(candidate, out found)) break;
                    end--;
                }
                if (found < 0) { pieces.Add(Id("[UNK]", 100)); break; }
                pieces.Add(found); start = end;
            }
            if (pieces.Count >= maximumLength - 1) break;
        }
        pieces.Add(Id("[SEP]", 102));
        return pieces.Take(maximumLength).ToArray();
    }

    private static IEnumerable<string> BasicTokens(string text)
    {
        var buffer = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            var chinese = rune.Value is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF;
            if (chinese || Rune.IsPunctuation(rune) || Rune.IsWhiteSpace(rune))
            {
                if (buffer.Length > 0) { yield return buffer.ToString(); buffer.Clear(); }
                if (chinese) yield return rune.ToString();
            }
            else buffer.Append(rune.ToString());
        }
        if (buffer.Length > 0) yield return buffer.ToString();
    }

    private long Id(string token, long fallback) => _vocabulary.GetValueOrDefault(token, fallback);
    private static float[] Normalize(float[] values)
    {
        var norm = MathF.Sqrt(values.Sum(value => value * value));
        if (norm > 0) for (var index = 0; index < values.Length; index++) values[index] /= norm;
        return values;
    }
    public void Dispose() { _inference.Dispose(); _session.Dispose(); }
}
