namespace Picer.Core.People;

public sealed record TrainingRelayRequest(string Method, string Path, string? Body);
public sealed record TrainingRelayResponse(int StatusCode, string Body);

/// <summary>Shared allowlist: callers cannot select an arbitrary local HTTP route.</summary>
public static class TrainingRelayContract
{
    public static bool IsAllowed(string method, string path)
    {
        if (path.Length > 512 || path.Contains('%') || path.Contains('\\') || path.Contains("..")) return false;
        var parts = path.Split('?', 2);
        var route = parts[0];
        if (parts.Length == 2 && (route != "archive" || !parts[1].StartsWith("limit=", StringComparison.Ordinal)
            || !int.TryParse(parts[1][6..], out var limit) || limit is < 1 or > 1000)) return false;
        if (method == "GET" && route is "algorithms" or "evaluation-dataset" or "signing-key" or "archive" or "runtime" or "model-rankings" or "model-rankings/rounds") return true;
        if (method == "POST" && route is "embeddings" or "datasets" or "jobs" or "model-rankings/baseline") return parts.Length == 1;
        var segments = route.Split('/');
        if (segments.Length < 2 || segments[1].Length == 0 || !segments[1].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        return parts.Length == 1 && ((method == "GET" && segments.Length == 2 && segments[0] is "jobs" or "artifacts")
            || (method == "POST" && segments.Length == 3 && segments[0] == "artifacts" && segments[2] == "applied")
            || (method == "POST" && segments.Length == 3 && segments[0] == "jobs" && segments[2] == "cancel")
            || (method == "GET" && segments.Length == 3 && segments[0] == "jobs" && segments[2] == "logs"));
    }
}
