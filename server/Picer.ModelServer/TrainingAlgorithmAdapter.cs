namespace Picer.ModelServer;

/// Algorithm-neutral contract. The ModelServer can select another adapter
/// without changing the evidence protocol or the client.
public interface ITrainingAlgorithmAdapter
{
    string Id { get; }
    string ModelVersion { get; }
    string DisplayName { get; }
    bool IsAvailable { get; }
    bool Supports(string requestedType);
}

public sealed class PrototypeCalibrationAdapter : ITrainingAlgorithmAdapter
{
    public string Id => "prototype-calibration";
    public string ModelVersion => "sface";
    public string DisplayName => "OpenCV SFace baseline";
    public bool IsAvailable => true;
    public bool Supports(string requestedType) =>
        string.Equals(requestedType, Id, StringComparison.OrdinalIgnoreCase);
}

/// <summary>ArcFace adapter contract. The actual ONNX/engine is selected by the
/// platform deployment; absence of the configured file is reported explicitly.</summary>
public sealed class ArcFaceAdapter : ITrainingAlgorithmAdapter
{
    public string Id => "arcface-prototype-calibration";
    public string ModelVersion => "arcface";
    public string DisplayName => "ArcFace high-accuracy candidate";
    public bool IsAvailable => File.Exists(Environment.GetEnvironmentVariable("PICER_ARCFACE_MODEL_PATH") ?? string.Empty);
    public bool Supports(string requestedType) => string.Equals(requestedType, Id, StringComparison.OrdinalIgnoreCase);
}

public sealed class AdaFaceAdapter : ITrainingAlgorithmAdapter
{
    public string Id => "adaface-prototype-calibration";
    public string ModelVersion => "adaface";
    public string DisplayName => "AdaFace quality-aware candidate";
    public bool IsAvailable => File.Exists(Environment.GetEnvironmentVariable("PICER_ADAFACE_MODEL_PATH") ?? string.Empty);
    public bool Supports(string requestedType) => string.Equals(requestedType, Id, StringComparison.OrdinalIgnoreCase);
}

/// <summary>InsightFace distribution adapter. It is kept separate from the
/// generic ArcFace adapter because detector/alignment and embedding versions
/// must be evaluated as one immutable stack.</summary>
public sealed class InsightFaceAdapter : ITrainingAlgorithmAdapter
{
    public string Id => "insightface-arcface-prototype-calibration";
    public string ModelVersion => "insightface-arcface";
    public string DisplayName => "InsightFace ArcFace stack";
    public bool IsAvailable => File.Exists(Environment.GetEnvironmentVariable("PICER_INSIGHTFACE_MODEL_PATH") ?? string.Empty);
    public bool Supports(string requestedType) => string.Equals(requestedType, Id, StringComparison.OrdinalIgnoreCase);
}
