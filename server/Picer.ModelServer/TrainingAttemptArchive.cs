using System.Text.Json;

namespace Picer.ModelServer;

/// <summary>Durable, append-only training memory. Raw embeddings are never written here.</summary>
public sealed class TrainingAttemptArchive
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public TrainingAttemptArchive()
    {
        var root = Environment.GetEnvironmentVariable("PICER_TRAINING_ARCHIVE_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "training-archive");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "attempts.jsonl");
    }

    public void Append(TrainingAttemptRecord record)
    {
        lock (_gate)
        {
            File.AppendAllText(_path, JsonSerializer.Serialize(record, _json) + Environment.NewLine);
        }
    }

    public IReadOnlyList<TrainingAttemptRecord> ReadRecent(int limit = 100)
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return [];
            return File.ReadLines(_path).Reverse().Take(Math.Clamp(limit, 1, 1000))
                .Select(line => JsonSerializer.Deserialize<TrainingAttemptRecord>(line, _json))
                .Where(item => item is not null).Cast<TrainingAttemptRecord>().ToArray();
        }
    }
}

public sealed record TrainingAttemptRecord(
    string DatasetId, string SnapshotId, string? JobId, string? ArtifactId,
    int Attempt, string ModelVersion, int SampleCount, int PersonCount,
    string Status, double? ValidationAccuracy, double? FalseAcceptRate,
    double? FalseRejectRate, string? AdvisorModel, string? AdvisorPromptVersion,
    string? TrainingHints, DateTime CreatedUtc);
