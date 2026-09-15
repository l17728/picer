namespace Picer.Core.People;

/// <summary>Resolves the local ModelServer credential without ever writing it to logs.</summary>
internal static class ModelServerApiKey
{
    public static string Load()
    {
        var configured = Environment.GetEnvironmentVariable("PICER_MODEL_SERVER_API_KEY")?.Trim();
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".picer", "model-server-key"),
            Path.Combine(Environment.GetEnvironmentVariable("PICER_DATA_ROOT") ?? string.Empty, "secrets", "model-server-api-key.txt"),
            @"D:\PicerData\secrets\model-server-api-key.txt"
        };
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } key) return key;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return string.Empty;
    }
}
