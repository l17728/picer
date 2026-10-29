using Picer.Core.Models;

namespace Picer.Core.Vision;

/// <summary>Materialized accepted descriptions for grouping outside database transactions.</summary>
public sealed class SceneDescriptionLookup(IReadOnlyList<SceneDescriptionEntry> entries)
{
    private readonly Dictionary<string, SceneGroupDescription> _byFingerprint = entries
        .ToDictionary(entry => entry.Description.Fingerprint, entry => entry.Description);

    public SceneGroupDescription? GetAccepted(string fingerprint) => _byFingerprint.GetValueOrDefault(fingerprint);

    public SceneGroupDescription? GetAcceptedForGroup(string directoryPath, IReadOnlyList<ImageRecord> images)
    {
        if (images.Count == 0) return null;
        var directory = Path.GetFullPath(directoryPath);
        var start = images.Min(SceneTimeClusterService.EffectiveTime);
        var end = images.Max(SceneTimeClusterService.EffectiveTime);
        SceneGroupDescription? best = null;
        var bestScore = double.MinValue;
        foreach (var entry in entries)
        {
            if (!string.Equals(entry.DirectoryPath, directory, StringComparison.OrdinalIgnoreCase) ||
                entry.StartUtc > end || entry.EndUtc < start) continue;
            var overlapStart = entry.StartUtc > start ? entry.StartUtc : start;
            var overlapEnd = entry.EndUtc < end ? entry.EndUtc : end;
            var unionSeconds = Math.Max(1, (Math.Max(entry.EndUtc.Ticks, end.Ticks) - Math.Min(entry.StartUtc.Ticks, start.Ticks)) / (double)TimeSpan.TicksPerSecond);
            var temporalScore = Math.Max(0, (overlapEnd - overlapStart).TotalSeconds) / unionSeconds;
            var countScore = 1d - Math.Min(1d, Math.Abs(entry.ImageCount - images.Count) / (double)Math.Max(entry.ImageCount, images.Count));
            var score = temporalScore * .7 + countScore * .3;
            if (score <= bestScore) continue;
            bestScore = score;
            best = entry.Description;
        }
        return best;
    }
}

public sealed record SceneDescriptionEntry(string DirectoryPath, DateTime StartUtc, DateTime EndUtc,
    int ImageCount, SceneGroupDescription Description);
