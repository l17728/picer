using System.Security.Cryptography;
using System.Collections.Concurrent;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using Picer.Core.Models;

namespace Picer.Core.Vision;

/// <summary>Local CPU face detector and embedder. It reads source images without modifying them.</summary>
public sealed class YuNetSFaceAnalyzer : IImageFaceAnalyzer, IDisposable
{
    private readonly Size _detectionSize;
    private readonly string _detectorPath;
    private readonly string _recognizerPath;
    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly Thread _worker;
    private bool _disposed;

    public YuNetSFaceAnalyzer(string detectorPath, string recognizerPath, int detectionEdge = 320)
    {
        _detectorPath = RequireModel(detectorPath, "YuNet");
        _recognizerPath = RequireModel(recognizerPath, "SFace");
        ModelVersion = $"yunet-{ShortHash(_detectorPath)}+sface-{ShortHash(_recognizerPath)}";
        _detectionSize = new Size(Math.Clamp(detectionEdge, 160, 1280), Math.Clamp(detectionEdge, 160, 1280));
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "Picer.FaceInference" };
        _worker.Start();
    }

    public string AnalyzerName => "face";
    public string ModelVersion { get; }

    public Task<FaceAnalysisResult> AnalyzeAsync(ImageRecord image, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<FaceAnalysisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        try { _queue.Add(new WorkItem(image, cancellationToken, completion), cancellationToken); }
        catch (InvalidOperationException) { throw new ObjectDisposedException(nameof(YuNetSFaceAnalyzer)); }
        return completion.Task;
    }

    private void WorkerLoop()
    {
        FaceDetectorYN? detector = null;
        Net? recognizer = null;
        try
        {
            detector = FaceDetectorYN.Create(_detectorPath, "", _detectionSize, 0.75f, 0.3f, 5000);
            recognizer = CvDnn.ReadNetFromOnnx(_recognizerPath)
                ?? throw new InvalidOperationException("Unable to load the SFace model.");
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                if (work.CancellationToken.IsCancellationRequested)
                {
                    work.Completion.TrySetCanceled(work.CancellationToken);
                    continue;
                }
                try { work.Completion.TrySetResult(Analyze(work.Image, work.CancellationToken, detector, recognizer, _detectionSize)); }
                catch (OperationCanceledException) { work.Completion.TrySetCanceled(work.CancellationToken); }
                catch (Exception exception) { work.Completion.TrySetException(exception); }
            }
        }
        catch (Exception exception)
        {
            while (_queue.TryTake(out var work)) work.Completion.TrySetException(exception);
        }
        finally
        {
            recognizer?.Dispose();
            detector?.Dispose();
        }
    }

    private static FaceAnalysisResult Analyze(ImageRecord image, CancellationToken cancellationToken,
        FaceDetectorYN detector, Net recognizer, Size detectionSize)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(image.Path)) throw new FileNotFoundException("The indexed image is not available.");

        using var source = Cv2.ImRead(image.Path, ImreadModes.Color);
        if (source.Empty()) throw new InvalidDataException("The image cannot be decoded.");
        using var input = new Mat();
        Cv2.Resize(source, input, detectionSize);
        using var detected = new Mat();
        detector.Detect(input, detected);

        var detectedRows = detected.Rows;
        var faces = new List<FaceDetection>(detectedRows);
        for (var row = 0; row < detectedRows; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var x = Math.Clamp(detected.At<float>(row, 0), 0, detectionSize.Width - 1);
            var y = Math.Clamp(detected.At<float>(row, 1), 0, detectionSize.Height - 1);
            var width = Math.Clamp(detected.At<float>(row, 2), 1, detectionSize.Width - x);
            var height = Math.Clamp(detected.At<float>(row, 3), 1, detectionSize.Height - y);
            var confidence = detected.At<float>(row, 14);
            var landmarks = new float[10];
            for (var index = 0; index < landmarks.Length; index += 2)
            {
                landmarks[index] = detected.At<float>(row, 4 + index) / detectionSize.Width;
                landmarks[index + 1] = detected.At<float>(row, 5 + index) / detectionSize.Height;
            }

            using var aligned = new Mat();
            var rect = new Rect((int)x, (int)y, Math.Max(1, (int)width), Math.Max(1, (int)height));
            rect.Width = Math.Min(rect.Width, input.Width - rect.X);
            rect.Height = Math.Min(rect.Height, input.Height - rect.Y);
            using var crop = new Mat(input, rect);
            Cv2.Resize(crop, aligned, new Size(112, 112));
            using var blob = CvDnn.BlobFromImage(aligned, 1d / 128d, new Size(112, 112),
                new Scalar(127.5, 127.5, 127.5), swapRB: true, crop: false);
            recognizer.SetInput(blob);
            using var feature = recognizer.Forward();
            if (!feature.GetArray<float>(out var embedding) || embedding.Length == 0)
                throw new InvalidDataException("SFace returned an empty feature vector.");
            Normalize(embedding);

            faces.Add(new FaceDetection(
                new NormalizedFaceBox(
                    x / detectionSize.Width,
                    y / detectionSize.Height,
                    width / detectionSize.Width,
                    height / detectionSize.Height),
                confidence,
                landmarks,
                embedding));
        }

        return new FaceAnalysisResult(faces
            .OrderBy(face => face.Box.Y + face.Box.Height / 2)
            .ThenBy(face => face.Box.X + face.Box.Width / 2)
            .ToArray());
    }

    private static void Normalize(float[] vector)
    {
        var norm = Math.Sqrt(vector.Sum(value => value * value));
        if (norm <= double.Epsilon) return;
        for (var index = 0; index < vector.Length; index++) vector[index] = (float)(vector[index] / norm);
    }

    private static string RequireModel(string path, string name)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) || new FileInfo(fullPath).Length == 0)
            throw new FileNotFoundException($"{name} model is missing.", fullPath);
        return fullPath;
    }

    private static string ShortHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream))[..12].ToLowerInvariant();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }

    private sealed record WorkItem(ImageRecord Image, CancellationToken CancellationToken,
        TaskCompletionSource<FaceAnalysisResult> Completion);
}
