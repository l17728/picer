using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Picer.Core.SemanticSearch;

public sealed class ChineseClipEncoder : IDisposable
{
    public const string ModelVersion = "OFA-Chinese-CLIP-ViT-B-16/Xenova-f269048-int8";
    private readonly InferenceSession _session;
    private readonly Dictionary<string, long> _vocabulary;

    public ChineseClipEncoder(string modelPath, string vocabularyPath)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("缺少中文 CLIP 模型", modelPath);
        if (!File.Exists(vocabularyPath)) throw new FileNotFoundException("缺少中文 CLIP 词表", vocabularyPath);
        _session = new InferenceSession(modelPath, new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL });
        _vocabulary = File.ReadLines(vocabularyPath).Select((token, id) => (token, id)).ToDictionary(x => x.token, x => (long)x.id, StringComparer.Ordinal);
    }

    public float[] EncodeText(string text)
    {
        var ids = Tokenize(text, 52);
        var input = new DenseTensor<long>(ids, [1, ids.Length]);
        var mask = new DenseTensor<long>(Enumerable.Repeat(1L, ids.Length).ToArray(), [1, ids.Length]);
        var dummyPixels = new DenseTensor<float>(new float[3 * 224 * 224], [1, 3, 224, 224]);
        using var output = _session.Run([NamedOnnxValue.CreateFromTensor("input_ids", input), NamedOnnxValue.CreateFromTensor("attention_mask", mask), NamedOnnxValue.CreateFromTensor("pixel_values", dummyPixels)], ["text_embeds"]);
        return Normalize(output.First().AsEnumerable<float>().ToArray());
    }

    public float[] EncodeImage(string path)
    {
        using var image = Image.Load<Rgb24>(path);
        image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(224, 224), Mode = ResizeMode.Stretch }));
        var values = new float[3 * 224 * 224];
        var mean = new[] { .48145466f, .4578275f, .40821073f };
        var std = new[] { .26862954f, .26130258f, .27577711f };
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < 224; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < 224; x++)
                {
                    var pixel = row[x]; var offset = y * 224 + x;
                    values[offset] = (pixel.R / 255f - mean[0]) / std[0];
                    values[224 * 224 + offset] = (pixel.G / 255f - mean[1]) / std[1];
                    values[2 * 224 * 224 + offset] = (pixel.B / 255f - mean[2]) / std[2];
                }
            }
        });
        var ids = new DenseTensor<long>(new long[] { 101, 102 }, [1, 2]);
        var mask = new DenseTensor<long>(new long[] { 1, 1 }, [1, 2]);
        var pixels = new DenseTensor<float>(values, [1, 3, 224, 224]);
        using var output = _session.Run([NamedOnnxValue.CreateFromTensor("input_ids", ids), NamedOnnxValue.CreateFromTensor("attention_mask", mask), NamedOnnxValue.CreateFromTensor("pixel_values", pixels)], ["image_embeds"]);
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
            var value = rune.Value;
            var chinese = value is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF;
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
    private static float[] Normalize(float[] values) { var norm = MathF.Sqrt(values.Sum(x => x * x)); if (norm > 0) for (var i = 0; i < values.Length; i++) values[i] /= norm; return values; }
    public void Dispose() => _session.Dispose();
}
