using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Picer.Core.Vision;

namespace Picer.App;

public sealed record RuntimeComponentSpec(string Name, string RelativePath, long Bytes, string Sha256, string License);
public sealed record RuntimeComponentStatus(RuntimeComponentSpec Spec, bool Exists, bool SizeMatches, bool HashMatches, string Detail)
{
    public bool Ready => Exists && SizeMatches && HashMatches;
    public string State => Ready ? "就绪" : Detail;
}

public sealed class RuntimeComponentService(string runtimeRoot)
{
    public static IReadOnlyList<RuntimeComponentSpec> RequiredComponents { get; } =
    [
        new("YuNet 人脸检测", "models/face/face_detection_yunet_2023mar.onnx", 232589, "8F2383E4DD3CFBB4553EA8718107FC0423210DC964F9F4280604804ED2552FA4", "MIT"),
        new("SFace 人脸识别", "models/face/face_recognition_sface_2021dec.onnx", 38696353, "0BA9FBFA01B5270C96627C4EF784DA859931E02F04419C829E83484087C34E79", "Apache-2.0"),
        new("FFmpeg", "ffmpeg/ffmpeg.exe", 102856192, "72A489ECCD008C2EC2C0A5856C5C75BC3D8BBFA90166C4566865C246445E6AA3", "GPL-3.0"),
        new("whisper.cpp", "whisper/whisper-cli.exe", 479232, "800A0FD754AFA75E109C7248286AD735670FB6B23D92CA5D12604647EF638A65", "MIT"),
        new("whisper.cpp 运行库", "whisper/whisper.dll", 1368064, "0A29E5824C7495185B833AD07DF7AB9CADF130A9BE848F967C6B88AECA971566", "MIT"),
        new("Whisper base-q5_1 模型", "models/whisper/ggml-base-q5_1.bin", 59707625, "422F1AE452ADE6F30A004D7E5C6A43195E4433BC370BF23FAC9CC591F01A8898", "MIT/model data"),
        new("Chinese-CLIP 中文语义模型", "models/chinese-clip/model_quantized.onnx", 190842404, "A2C2849329EAFB24100ACEC5A9421A39F8CB445296444C5C98855B69E5E7A751", "MIT/model data"),
        new("Chinese-CLIP 中文词表", "models/chinese-clip/vocab.txt", 109540, "45BBAC6B341C319ADC98A532532882E91A9CEFC0329AA57BAC9AE761C27B291C", "MIT/model data"),
        new("Florence-2 视觉编码器 INT8", "models/florence-2-base-ft/vision_encoder_quantized.onnx", 93746540, "3B79D54F23F666F731549DB23CB070C35A979CE19CBD9720E90E67A78DC9768C", "MIT/model data"),
        new("Florence-2 文本编码器 INT8", "models/florence-2-base-ft/encoder_model_quantized.onnx", 43651493, "F4AD7A68F1FB875D3BCF735EA14A7021B7BA7E83BAF7CF10289881B4ED6D9B85", "MIT/model data"),
        new("Florence-2 词嵌入 INT8", "models/florence-2-base-ft/embed_tokens_quantized.onnx", 39390433, "6B2258DB1C8EE9B160576CCDE3CD3814D83A2EDAED0DD1C6CA9FF3C38FA62214", "MIT/model data"),
        new("Florence-2 合并解码器 INT8", "models/florence-2-base-ft/decoder_model_merged_quantized.onnx", 98177697, "F22F52F980C33DF0EFA15932C2F3DB6D9D3595CE6387ECA938B8CFE23DC4C641", "MIT/model data"),
        new("Florence-2 自回归解码器 INT8", "models/florence-2-base-ft/decoder_model_quantized.onnx", 97760937, "C529B26BAFCE2EE76F886F3A0E374BB646B07A6D8B7640FD8A50D7A48843DD67", "MIT/model data"),
        new("Florence-2 配置", "models/florence-2-base-ft/config.json", 5432, "D90C22ED72EB55291F183FCD9B98EBD3BD3D92BFCFFB6C7F6E1606085E793525", "MIT/model data"),
        new("Florence-2 预处理配置", "models/florence-2-base-ft/preprocessor_config.json", 2673, "C892857E34A7082284983A7717717D39C9BF7E574F1F41D80D4C918C97502EFA", "MIT/model data"),
        new("Florence-2 分词器", "models/florence-2-base-ft/tokenizer.json", 2297961, "D69DCDB2323E124AC4F800CB9863DDCCEA0D7BB11E16125E8DF3BD60F2F8AEAC", "MIT/model data"),
        new("Florence-2 词表", "models/florence-2-base-ft/vocab.json", 1099884, "394FDC63C71AABE0A9B97117F5D62FB5FCC4D59B2B3EA929A3929E6A53217B3C", "MIT/model data"),
        new("Florence-2 合并规则", "models/florence-2-base-ft/merges.txt", 456318, "1CE1664773C50F3E0CC8842619A93EDC4624525B728B188A9E0BE33B7726ADC5", "MIT/model data"),
        new("Florence-2 生成配置", "models/florence-2-base-ft/generation_config.json", 292, "7B8EB17BBD6CF8A07F619AD83AE03881EFF05B6B9237BAB89005B40E77783C29", "MIT/model data"),
        new("Florence-2 分词配置", "models/florence-2-base-ft/tokenizer_config.json", 197658, "D8E64607233CB53B619FB46664F6CAD08176C26E0E8735B2D30D888364F19600", "MIT/model data"),
        new("Florence-2 特殊词元", "models/florence-2-base-ft/special_tokens_map.json", 146627, "72FF172DC769BC1551B1B4211628CE3271643BC60379E4DA45D85A9BE9332C39", "MIT/model data"),
        new("Florence-2 附加词元", "models/florence-2-base-ft/added_tokens.json", 22410, "BA67457C577895525996E7A42D7DA60A05856506EA4FD25FF9A023A64662F74B", "MIT/model data"),
        new("DirectML x64 运行库", "../DirectML.dll", 18527776, "9C9E6D822561C6C41B90E6994B3E8857CF1D66DBFB1E0C4C799C7C89B4E92DA1", "MIT"),
        new("ONNX Runtime DirectML", "../onnxruntime.dll", 16468024, "F722DD91A311B9FA4B7F2341D22C379A72FB7BC5B639DF18B64821F23CDDE14B", "MIT")
    ];

    public string RuntimeRoot { get; } = Path.GetFullPath(runtimeRoot);

    public IReadOnlyList<string> GetMissingComponentNames() => RequiredComponents
        .Where(spec => !File.Exists(Resolve(spec)))
        .Select(spec => spec.Name)
        .ToArray();

    public async Task<IReadOnlyList<RuntimeComponentStatus>> VerifyAsync(
        IProgress<(int Completed, int Total, string Name)>? progress = null, CancellationToken cancellationToken = default)
    {
        var results = new List<RuntimeComponentStatus>();
        foreach (var spec in RequiredComponents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Resolve(spec);
            if (!File.Exists(path)) results.Add(new(spec, false, false, false, "缺失"));
            else
            {
                var sizeMatches = new FileInfo(path).Length == spec.Bytes;
                var hash = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
                var hashMatches = string.Equals(hash, spec.Sha256, StringComparison.OrdinalIgnoreCase);
                results.Add(new(spec, true, sizeMatches, hashMatches,
                    !sizeMatches ? "文件大小不符" : !hashMatches ? "SHA-256 不符" : "就绪"));
            }
            progress?.Report((results.Count, RequiredComponents.Count, spec.Name));
        }
        return results;
    }

    public async Task<IReadOnlyList<string>> SelfTestAsync(CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        var verified = await VerifyAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (verified.Any(status => !status.Ready))
            return verified.Where(status => !status.Ready).Select(status => $"{status.Spec.Name}：{status.Detail}").ToArray();

        using (var analyzer = new YuNetSFaceAnalyzer(
            Resolve(RequiredComponents[0]), Resolve(RequiredComponents[1])))
            messages.Add($"人脸模型：已加载（{analyzer.ModelVersion}）");
        messages.Add(await RunVersionAsync(Resolve(RequiredComponents[2]), "-version", "FFmpeg", cancellationToken).ConfigureAwait(false));
        messages.Add(await RunVersionAsync(Resolve(RequiredComponents[3]), "--version", "whisper.cpp", cancellationToken).ConfigureAwait(false));
        return messages;
    }

    public string Resolve(RuntimeComponentSpec spec) => Path.GetFullPath(Path.Combine(RuntimeRoot, spec.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static async Task<string> RunVersionAsync(string executable, string arguments, string name, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable, arguments) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var text = (await outputTask.ConfigureAwait(false) + "\n" + await errorTask.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{name} 自检失败，退出码 {process.ExitCode}。");
        return $"{name}：{text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "自检通过"}";
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}
