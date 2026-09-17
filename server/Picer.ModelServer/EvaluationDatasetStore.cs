using System.Text.Json;

namespace Picer.ModelServer;

/// <summary>Durable, user-editable immutable evaluation snapshot manifest.
/// Image bytes stay in the platform store so repeated baseline runs use exactly
/// the same samples until the user explicitly replaces/removes one.</summary>
public sealed class EvaluationDatasetStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private DatasetRequest? _dataset;

    public EvaluationDatasetStore()
    {
        var root = Environment.GetEnvironmentVariable("PICER_TRAINING_ARCHIVE_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "training-archive");
        Directory.CreateDirectory(root); _path = Path.Combine(root, "evaluation-dataset.json");
        if (File.Exists(_path))
            _dataset = JsonSerializer.Deserialize<DatasetRequest>(File.ReadAllText(_path), _json);
    }

    public DatasetRequest? Get() { lock (_gate) return _dataset; }
    public void Replace(DatasetRequest dataset)
    {
        lock (_gate) { _dataset = dataset with { Scenario = "baseline" }; Persist(); }
    }
    public bool Remove(long faceId)
    {
        lock (_gate)
        {
            if (_dataset?.Samples is not { Length: > 0 }) return false;
            var remaining = _dataset.Samples.Where(sample => sample.FaceId != faceId).ToArray();
            if (remaining.Length == _dataset.Samples.Length) return false;
            _dataset = _dataset with { Samples = remaining }; Persist(); return true;
        }
    }
    private void Persist() => File.WriteAllText(_path, JsonSerializer.Serialize(_dataset, _json));
}
