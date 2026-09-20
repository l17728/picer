namespace Picer.Core.People;

/// <summary>Authoritative client-side transition rules for asynchronous training jobs.</summary>
public static class TrainingJobStateMachine
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Transitions =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["queued"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "queued", "running", "canceled", "failed", "recoverable" },
            ["running"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "running", "completed", "failed", "canceled", "recoverable" },
            ["recoverable"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "queued", "running", "canceled", "failed" },
            ["completed"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "completed" },
            ["failed"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "failed", "queued" },
            ["canceled"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "canceled", "queued" }
        };

    public static bool IsTerminal(string status) =>
        string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase);

    public static bool CanTransition(string? current, string? next) =>
        !string.IsNullOrWhiteSpace(current) && !string.IsNullOrWhiteSpace(next) &&
        Transitions.TryGetValue(current, out var allowed) && allowed.Contains(next);

    public static string Normalize(string? status) => string.IsNullOrWhiteSpace(status) ? "queued" : status.Trim().ToLowerInvariant();

    public static TrainingJobStateTransition Validate(string jobId, string? current, string? next, DateTime? occurredUtc = null)
    {
        var from = Normalize(current);
        var to = Normalize(next);
        if (!CanTransition(from, to))
            throw new InvalidOperationException($"Training job '{jobId}' cannot transition from '{from}' to '{to}'.");
        return new(jobId, from, to, occurredUtc?.ToUniversalTime() ?? DateTime.UtcNow);
    }
}

public sealed record TrainingJobStateTransition(string JobId, string From, string To, DateTime OccurredUtc);
