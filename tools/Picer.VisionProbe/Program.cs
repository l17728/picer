using System.Diagnostics;
using System.Text.Json;
using OpenCvSharp;
using OpenCvSharp.Dnn;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: Picer.VisionProbe <YuNet.onnx> <SFace.onnx> <image-or-directory> [max-images]");
    return 2;
}

var detectorPath = Path.GetFullPath(args[0]);
var recognizerPath = Path.GetFullPath(args[1]);
var inputPath = Path.GetFullPath(args[2]);
var maxImages = args.Length > 3 && int.TryParse(args[3], out var parsed) ? parsed : 30;
var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp" };
var paths = File.Exists(inputPath)
    ? new[] { inputPath }
    : Directory.EnumerateFiles(inputPath, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
        .Where(path => extensions.Contains(Path.GetExtension(path))).Take(maxImages).ToArray();

using var detector = FaceDetectorYN.Create(detectorPath, "", new Size(320, 320), 0.75f, 0.3f, 5000);
using var recognizer = CvDnn.ReadNetFromOnnx(recognizerPath) ?? throw new InvalidOperationException("Unable to load SFace model");
var results = new List<object>();
var total = Stopwatch.StartNew();
var totalFaces = 0;

foreach (var path in paths)
{
    using var image = Cv2.ImRead(path);
    if (image.Empty()) continue;
    using var detectionInput = new Mat();
    Cv2.Resize(image, detectionInput, new Size(320, 320));
    using var faces = new Mat();
    var watch = Stopwatch.StartNew();
    detector.Detect(detectionInput, faces);
    var detectMs = watch.Elapsed.TotalMilliseconds;
    var featureMs = 0d;
    var dimensions = new List<int>();
    for (var row = 0; row < faces.Rows; row++)
    {
        var x = Math.Clamp((int)faces.At<float>(row, 0), 0, detectionInput.Width - 1);
        var y = Math.Clamp((int)faces.At<float>(row, 1), 0, detectionInput.Height - 1);
        var width = Math.Clamp((int)faces.At<float>(row, 2), 1, detectionInput.Width - x);
        var height = Math.Clamp((int)faces.At<float>(row, 3), 1, detectionInput.Height - y);
        using var crop = new Mat(detectionInput, new Rect(x, y, width, height));
        using var aligned = new Mat();
        Cv2.Resize(crop, aligned, new Size(112, 112));
        using var blob = CvDnn.BlobFromImage(aligned, 1d / 128d, new Size(112, 112), new Scalar(127.5, 127.5, 127.5), true, false);
        watch.Restart();
        recognizer.SetInput(blob);
        using var feature = recognizer.Forward();
        featureMs += watch.Elapsed.TotalMilliseconds;
        dimensions.Add((int)feature.Total());
        totalFaces++;
    }
    results.Add(new { path, width = image.Width, height = image.Height, faces = faces.Rows, detectMs, featureMs, dimensions });
    Console.WriteLine($"{Path.GetFileName(path)}: faces={faces.Rows}, detect={detectMs:F1} ms, embeddings={featureMs:F1} ms");
}

var report = new
{
    runtime = Cv2.GetVersionString(),
    machine = Environment.MachineName,
    processorCount = Environment.ProcessorCount,
    detectorBytes = new FileInfo(detectorPath).Length,
    recognizerBytes = new FileInfo(recognizerPath).Length,
    images = results.Count,
    totalFaces,
    elapsedMs = total.Elapsed.TotalMilliseconds,
    workingSetMb = Environment.WorkingSet / 1024d / 1024d,
    results
};
Console.WriteLine("REPORT_JSON=" + JsonSerializer.Serialize(report));
return 0;
