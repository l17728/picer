using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;

namespace Picer.Core.Logging;

public static class AppLogger
{
    private static readonly object _lock = new();
    private static string _logDir = "";
    private static readonly AsyncLocal<OperationScope?> CurrentOperation = new();

    public static void Initialize(string logDir)
    {
        _logDir = logDir;
        Directory.CreateDirectory(logDir);
    }

    public static void Info(string message) => Log("INFO", message);
    public static void Warn(string message) => Log("WARN", message);
    public static void Error(string message, Exception? ex = null)
    {
        var msg = ex is not null ? $"{message}: {ex.Message}\n{ex.StackTrace}" : message;
        Log("ERROR", msg);
    }

    /// <summary>
    /// Starts a correlation scope for a user-visible operation. The normal text
    /// log remains backward compatible; a redacted JSONL operation log is also
    /// written so a failure can be reconstructed by operation id and duration.
    /// Never pass names, questions, credentials or embeddings as details.
    /// </summary>
    public static OperationScope BeginOperation(string name, params (string Key, object? Value)[] details)
    {
        var scope = new OperationScope(name, details);
        CurrentOperation.Value = scope;
        Log("INFO", $"operation.start name={name}; operationId={scope.OperationId}");
        return scope;
    }

    public static string? CurrentOperationId => CurrentOperation.Value?.OperationId;

    public sealed class OperationScope : IDisposable
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly string _name;
        private readonly Dictionary<string, object?> _details;
        private int _ended;

        internal OperationScope(string name, IReadOnlyList<(string Key, object? Value)> details)
        {
            _name = string.IsNullOrWhiteSpace(name) ? "unnamed" : name.Trim();
            OperationId = Guid.NewGuid().ToString("N");
            _details = details.Where(x => !string.IsNullOrWhiteSpace(x.Key))
                .ToDictionary(x => x.Key, x => SanitizeField(x.Key, x.Value), StringComparer.OrdinalIgnoreCase);
        }

        public string OperationId { get; }

        public void Complete(params (string Key, object? Value)[] result)
            => End("completed", null, result);

        public void Fail(Exception exception)
            => End("failed", exception, []);

        public void Cancel(string? reason = null)
            => End("canceled", null, string.IsNullOrWhiteSpace(reason) ? [] : [("reason", reason)]);

        public void Dispose()
        {
            // Dispose is the safe default for early returns; explicit Fail or
            // Cancel is used by catch/cancellation paths.
            End("completed", null, []);
        }

        private void End(string status, Exception? exception, IReadOnlyList<(string Key, object? Value)> result)
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0) return;
            var fields = new Dictionary<string, object?>(_details, StringComparer.OrdinalIgnoreCase);
            foreach (var item in result.Where(x => !string.IsNullOrWhiteSpace(x.Key))) fields[item.Key] = SanitizeField(item.Key, item.Value);
            if (exception is not null) fields["exceptionType"] = exception.GetType().FullName;
            WriteOperation(new
            {
                timestampUtc = DateTime.UtcNow,
                level = status == "failed" ? "ERROR" : "INFO",
                operationId = OperationId,
                name = _name,
                status,
                durationMs = _watch.ElapsedMilliseconds,
                processId = Environment.ProcessId,
                threadId = Environment.CurrentManagedThreadId,
                details = fields
            });
            if (ReferenceEquals(CurrentOperation.Value, this)) CurrentOperation.Value = null;
        }
    }

    private static void Log(string level, string message)
    {
        if (string.IsNullOrEmpty(_logDir)) return;
        try
        {
            var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var line = $"[{ts}] {level}: {Redact(message)}";
            var file = Path.Combine(_logDir, $"picer-{DateTime.Now:yyyy-MM-dd}.log");
            lock (_lock)
            {
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
        catch { }
    }

    private static void WriteOperation(object value)
    {
        if (string.IsNullOrEmpty(_logDir)) return;
        try
        {
            var line = JsonSerializer.Serialize(value);
            var file = Path.Combine(_logDir, $"picer-operations-{DateTime.Now:yyyy-MM-dd}.jsonl");
            lock (_lock) File.AppendAllText(file, line + Environment.NewLine);
        }
        catch { }
    }

    private static object? SanitizeValue(object? value)
    {
        if (value is null) return null;
        if (value is string text) return Redact(text.Length > 256 ? text[..256] : text);
        if (value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
            return value;
        return Redact(value.ToString() ?? "");
    }

    private static object? SanitizeField(string key, object? value)
        => key.Contains("token", StringComparison.OrdinalIgnoreCase)
            || key.Contains("password", StringComparison.OrdinalIgnoreCase)
            || key.Contains("apikey", StringComparison.OrdinalIgnoreCase)
            || key.Contains("credential", StringComparison.OrdinalIgnoreCase)
            ? "[REDACTED]"
            : SanitizeValue(value);

    private static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var result = value;
        foreach (var marker in new[] { "Bearer ", "apiKey=", "apikey=", "token=", "password=" })
        {
            var start = result.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) continue;
            var valueStart = start + marker.Length;
            var end = result.IndexOfAny([' ', ';', ',', '\r', '\n'], valueStart);
            if (end < 0) end = result.Length;
            result = result[..valueStart] + "[REDACTED]" + result[end..];
        }
        return result;
    }
}
