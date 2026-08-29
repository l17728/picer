using Picer.Core.SemanticSearch;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Picer.Tests;

public sealed class ClipSemanticTests
{
    [Fact]
    public void BundledChineseClip_EncodesChineseTextOffline()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var model = Path.Combine(root, "third_party", "runtime", "models", "chinese-clip", "model_quantized.onnx");
        var vocabulary = Path.Combine(root, "third_party", "runtime", "models", "chinese-clip", "vocab.txt");
        using var encoder = new ChineseClipEncoder(model, vocabulary);
        var vector = encoder.EncodeText("海边看日落");
        Assert.Equal(512, vector.Length);
        Assert.InRange(Math.Sqrt(vector.Sum(x => x * x)), 0.999, 1.001);
    }

    [Fact]
    public void BundledChineseClip_EncodesImageOffline()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var model = Path.Combine(root, "third_party", "runtime", "models", "chinese-clip", "model_quantized.onnx");
        var vocabulary = Path.Combine(root, "third_party", "runtime", "models", "chinese-clip", "vocab.txt");
        var path = Path.Combine(Path.GetTempPath(), $"picer-clip-{Guid.NewGuid():N}.png");
        try
        {
            using (var image = new Image<Rgb24>(32, 32, new Rgb24(40, 120, 220))) image.SaveAsPng(path);
            using var encoder = new ChineseClipEncoder(model, vocabulary);
            var vector = encoder.EncodeImage(path);
            Assert.Equal(512, vector.Length);
            Assert.InRange(Math.Sqrt(vector.Sum(x => x * x)), 0.999, 1.001);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
