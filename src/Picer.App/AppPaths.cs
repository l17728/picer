using System.IO;
using System.Text.Json;

namespace Picer.App;

public static class AppPaths
{
    private static string BootstrapDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Picer", "bootstrap");
    private static string DataLocationPath => Path.Combine(BootstrapDirectory, "data-location.json");
    public static string RuntimeRoot => Path.Combine(AppContext.BaseDirectory, "runtime");

    public static string DataRoot
    {
        get
        {
            if (Environment.GetEnvironmentVariable("PICER_DATA_ROOT") is { Length: > 0 } isolatedRoot) return Path.GetFullPath(isolatedRoot);
            try { if (File.Exists(DataLocationPath) && JsonSerializer.Deserialize<DataLocation>(File.ReadAllText(DataLocationPath)) is { Path.Length: > 0 } configured) return Path.GetFullPath(configured.Path); } catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "picer");
        }
    }
    public static void SetDataRoot(string path) { Directory.CreateDirectory(BootstrapDirectory); File.WriteAllText(DataLocationPath, JsonSerializer.Serialize(new DataLocation(Path.GetFullPath(path)))); }

    public static string DbPath => Path.Combine(DataRoot, "picer.db");

    public static string ThumbnailDir => Path.Combine(DataRoot, "thumbs");

    public static string ModelRoot => Environment.GetEnvironmentVariable("PICER_MODEL_ROOT") is { Length: > 0 } modelRoot
        ? Path.GetFullPath(modelRoot)
        : Path.Combine(RuntimeRoot, "models", "face");

    public static string YuNetModelPath => Path.Combine(ModelRoot, "face_detection_yunet_2023mar.onnx");

    public static string SFaceModelPath => Path.Combine(ModelRoot, "face_recognition_sface_2021dec.onnx");

    public static string FaceThumbnailDir => Path.Combine(DataRoot, "face-thumbs");

    public static string BundledFfmpegPath => Path.Combine(RuntimeRoot, "ffmpeg", "ffmpeg.exe");
    public static string BundledWhisperPath => Path.Combine(RuntimeRoot, "whisper", "whisper-cli.exe");
    public static string BundledWhisperModelPath => Path.Combine(RuntimeRoot, "models", "whisper", "ggml-base-q5_1.bin");
    public static string ChineseClipModelPath => Path.Combine(RuntimeRoot, "models", "chinese-clip", "model_quantized.onnx");
    public static string ChineseClipVocabularyPath => Path.Combine(RuntimeRoot, "models", "chinese-clip", "vocab.txt");
    public static string FlorenceModelRoot => Path.Combine(RuntimeRoot, "models", "florence-2-base-ft");
    public static string FlorenceRuntimeSettingsPath => Path.Combine(DataRoot, "florence-runtime.json");

    public static string CloudVisionSettingsPath => Path.Combine(DataRoot, "cloud-vision.json");
    public static string DirectoryClassificationSettingsPath => Path.Combine(DataRoot, "directory-classification.json");
    private sealed record DataLocation(string Path);
}
