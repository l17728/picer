namespace Picer.Core.Logging;

public static class AnalysisTrace
{
    private static readonly AsyncLocal<string?> CurrentValue = new();
    public static string? Current => CurrentValue.Value;
    public static IDisposable Begin(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("invalid_trace_id");
        var previous = CurrentValue.Value; CurrentValue.Value = id;
        return new Scope(previous);
    }
    private sealed class Scope(string? previous) : IDisposable
    { public void Dispose() => CurrentValue.Value = previous; }
}
