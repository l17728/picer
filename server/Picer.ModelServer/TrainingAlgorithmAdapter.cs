namespace Picer.ModelServer;

/// Algorithm-neutral contract. The ModelServer can select another adapter
/// without changing the evidence protocol or the client.
public interface ITrainingAlgorithmAdapter
{
    string Id { get; }
    string ModelVersion { get; }
    bool Supports(string requestedType);
}

public sealed class PrototypeCalibrationAdapter : ITrainingAlgorithmAdapter
{
    public string Id => "prototype-calibration";
    public string ModelVersion => "sface";
    public bool Supports(string requestedType) =>
        string.Equals(requestedType, Id, StringComparison.OrdinalIgnoreCase);
}
