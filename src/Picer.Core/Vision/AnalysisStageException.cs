namespace Picer.Core.Vision;

public enum AnalysisStage { Scan, Preprocess, Model, Save }

/// <summary>Stable failure metadata for retry UI; never derives stage by guessing exception text.</summary>
public sealed class AnalysisStageException(AnalysisStage stage, string errorCode, bool retryable, Exception? inner = null)
    : InvalidOperationException($"{stage}|{errorCode}|{retryable}", inner)
{
    public AnalysisStage Stage { get; } = stage;
    public string ErrorCode { get; } = errorCode;
    public bool Retryable { get; } = retryable;

    public static AnalysisStageException Wrap(AnalysisStage stage, Exception exception)
    {
        if (exception is AnalysisStageException known) return known;
        if (exception is VisionStreamException streamFailure)
            return new(stage, streamFailure.ErrorCode, true, exception);
        var code = exception switch
        {
            System.Net.Http.HttpRequestException { StatusCode: { } status } => "HTTP_" + (int)status,
            System.Net.Http.HttpRequestException request when FindInner<System.Net.Sockets.SocketException>(request) is { } socket => "socket_" + (int)socket.SocketErrorCode,
            System.Net.Sockets.SocketException socket => "socket_" + (int)socket.SocketErrorCode,
            Microsoft.Data.Sqlite.SqliteException sql => $"SQLite_{sql.SqliteErrorCode}_{sql.SqliteExtendedErrorCode}",
            FileNotFoundException => "input_file_missing",
            DirectoryNotFoundException => "input_directory_missing",
            IOException => "io_failure",
            UnauthorizedAccessException => "access_denied",
            OperationCanceledException or TimeoutException => "request_timeout",
            System.Text.Json.JsonException => "invalid_model_json",
            InvalidDataException when exception.Message == "The vision API returned empty content." => "empty_model_response",
            InvalidDataException when exception.Message == "The vision API returned invalid JSON." => "invalid_model_json",
            _ => exception.GetType().Name
        };
        var retryable = exception is not (ArgumentException or NotSupportedException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException);
        if (exception is System.Net.Http.HttpRequestException { StatusCode: { } statusCode } && (int)statusCode is 400 or 401 or 403 or 404) retryable = false;
        return new(stage, code, retryable, exception);
    }

    private static T? FindInner<T>(Exception exception) where T : Exception
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
            if (current is T match) return match;
        return null;
    }

    public static AnalysisStageException FromStored(string? value)
    {
        const string prefix = nameof(AnalysisStageException) + ": ";
        var detail = value?.StartsWith(prefix, StringComparison.Ordinal) == true ? value[prefix.Length..] : value;
        var parts = detail?.Split('|');
        if (parts is { Length: 3 } && Enum.TryParse<AnalysisStage>(parts[0], out var stage) && bool.TryParse(parts[2], out var retryable))
            return new(stage, parts[1], retryable);
        // Preserve legacy recorded detail for display without fabricating a more specific stage.
        return new(AnalysisStage.Model, value ?? "analysis_failed", true);
    }
}
