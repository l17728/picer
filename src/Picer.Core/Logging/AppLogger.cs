using System.IO;

namespace Picer.Core.Logging;

public static class AppLogger
{
    private static readonly object _lock = new();
    private static string _logDir = "";

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

    private static void Log(string level, string message)
    {
        if (string.IsNullOrEmpty(_logDir)) return;
        try
        {
            var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var line = $"[{ts}] {level}: {message}";
            var file = Path.Combine(_logDir, $"picer-{DateTime.Now:yyyy-MM-dd}.log");
            lock (_lock)
            {
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
        catch { }
    }
}
