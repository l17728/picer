using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

[assembly: InternalsVisibleTo("Picer.Tests")]

namespace Picer.IndexService;

/// <summary>Latest-state index; legacy JSONL is imported incrementally only at startup.</summary>
internal sealed class TaskJournal
{
    private readonly string _connectionString;
    private readonly object _gate = new();
    private int _writes;
    internal static readonly AsyncLocal<long?> VideoContext = new();
    public TaskJournal(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataRoot, "service-tasks.sqlite"), Pooling = false, DefaultTimeout = 5 }.ToString();
        using var connection = Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS latest(request_id TEXT PRIMARY KEY,operation TEXT NOT NULL,state TEXT NOT NULL,error TEXT,utc TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS latest_utc ON latest(utc DESC);
                CREATE INDEX IF NOT EXISTS latest_state_utc ON latest(state,utc);
                CREATE TABLE IF NOT EXISTS task_media(request_id TEXT PRIMARY KEY,video_id INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS task_media_video ON task_media(video_id);
                CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value INTEGER NOT NULL);
                """;
            command.ExecuteNonQuery();
        }
        ImportLegacy(connection, Path.Combine(dataRoot, "service-tasks.jsonl"));
        Trim(connection);
    }

    private SqliteConnection Open() { var connection = new SqliteConnection(_connectionString); connection.Open(); return connection; }

    private static void Save(SqliteConnection connection, string requestId, string operation, string state, string? error, string utc, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO latest(request_id,operation,state,error,utc) VALUES($id,$op,$state,$error,$utc) ON CONFLICT(request_id) DO UPDATE SET operation=excluded.operation,state=excluded.state,error=excluded.error,utc=excluded.utc";
        command.Parameters.AddWithValue("$id", requestId); command.Parameters.AddWithValue("$op", operation);
        command.Parameters.AddWithValue("$state", state); command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value); command.Parameters.AddWithValue("$utc", utc);
        command.ExecuteNonQuery();
    }

    private static void ImportLegacy(SqliteConnection connection, string path)
    {
        if (!File.Exists(path)) return;
        long offset;
        using (var command = connection.CreateCommand()) { command.CommandText = "SELECT value FROM settings WHERE key='legacy_offset'"; offset = Convert.ToInt64(command.ExecuteScalar() ?? 0L); }
        // The retired log has no live writer in this process. Hold out writers while
        // importing and compact only after the latest-state transaction is durable.
        using var input = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        if (offset > input.Length) offset = 0;
        if (offset == input.Length) return;
        input.Position = offset;
        using var reader = new StreamReader(input);
        using var transaction = connection.BeginTransaction();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                var value = json.RootElement;
                if (!value.TryGetProperty("requestId", out var id) || id.GetString() is not { Length: > 0 } requestId) continue;
                Save(connection, requestId, value.TryGetProperty("operation", out var operation) ? operation.GetString() ?? "" : "",
                    value.TryGetProperty("state", out var state) ? state.GetString() ?? "unknown" : "unknown",
                    value.TryGetProperty("error", out var error) ? error.GetString() : null,
                    value.TryGetProperty("utc", out var utc) ? utc.GetString() ?? DateTime.UtcNow.ToString("O") : DateTime.UtcNow.ToString("O"), transaction);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException) { /* skip malformed legacy lines */ }
        }
        using var checkpoint = connection.CreateCommand();
        checkpoint.Transaction = transaction;
        checkpoint.CommandText = "INSERT INTO settings(key,value) VALUES('legacy_offset',$offset) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        checkpoint.Parameters.AddWithValue("$offset", 0L); checkpoint.ExecuteNonQuery();
        transaction.Commit();
        input.SetLength(0);
        input.Flush(flushToDisk: true);
    }

    public void MarkInterrupted()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE latest SET state='abandoned',error='service_restart',utc=$utc WHERE state IN ('queued','running','retry_requested')";
            command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O")); command.ExecuteNonQuery();
        }
    }

    public void Append(string requestId, string operation, string state, string? error = null)
    {
        if (operation == "video.purge-cache") return;
        lock (_gate)
        {
            using var connection = Open();
            Save(connection, requestId, operation, state, error, DateTime.UtcNow.ToString("O"));
            if (VideoContext.Value is long videoId)
            {
                using var link = connection.CreateCommand();
                link.CommandText = "INSERT INTO task_media(request_id,video_id) VALUES($id,$video) ON CONFLICT(request_id) DO UPDATE SET video_id=excluded.video_id";
                link.Parameters.AddWithValue("$id", requestId); link.Parameters.AddWithValue("$video", videoId); link.ExecuteNonQuery();
            }
            if (++_writes % 256 == 0) Trim(connection);
        }
    }

    public IReadOnlyList<JsonElement> Read(int limit = 200)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT request_id,operation,state,error,utc FROM latest ORDER BY utc DESC LIMIT $limit";
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2000));
            using var reader = command.ExecuteReader(); var result = new List<JsonElement>();
            while (reader.Read()) result.Add(JsonSerializer.SerializeToElement(new { requestId = reader.GetString(0), operation = reader.GetString(1), state = reader.GetString(2), error = reader.IsDBNull(3) ? null : reader.GetString(3), utc = reader.GetString(4) }));
            result.Reverse(); return result;
        }
    }

    public bool RequestRetry(string requestId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE latest SET state='retry_requested',utc=$utc WHERE request_id=$id AND state IN ('failed','abandoned')";
            command.Parameters.AddWithValue("$id", requestId); command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            return command.ExecuteNonQuery() == 1;
        }
    }

    public IReadOnlyList<string> GetVideoRequestIds(long videoId)
    {
        lock (_gate)
        {
            using var connection = Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT request_id FROM latest WHERE operation IN ($probe,$audio,$frames,$transcribe,$people) UNION SELECT request_id FROM task_media WHERE video_id=$video";
            command.Parameters.AddWithValue("$video", videoId);
            foreach (var (parameter, operation) in new[] { ("$probe", "probe"), ("$audio", "audio"), ("$frames", "keyframes"), ("$transcribe", "transcribe"), ("$people", "people") })
                command.Parameters.AddWithValue(parameter, $"/api/service/v1/video-{operation}/{videoId}");
            using var reader = command.ExecuteReader(); var ids = new List<string>();
            while (reader.Read()) ids.Add(reader.GetString(0)); return ids;
        }
    }

    public void ForgetRequests(IEnumerable<string> requestIds)
    {
        lock (_gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            foreach (var id in requestIds.Distinct())
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "DELETE FROM latest WHERE request_id=$id; DELETE FROM task_media WHERE request_id=$id"; command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
            }
            transaction.Commit();
            using var vacuum = connection.CreateCommand(); vacuum.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); VACUUM;"; vacuum.ExecuteNonQuery();
        }
    }

    private static void Trim(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM latest WHERE utc<$cutoff AND state NOT IN ('queued','running','retry_requested');
            DELETE FROM latest WHERE request_id IN (SELECT request_id FROM latest ORDER BY utc DESC LIMIT -1 OFFSET 100000);
            DELETE FROM task_media WHERE request_id NOT IN (SELECT request_id FROM latest);
            """;
        command.Parameters.AddWithValue("$cutoff", DateTime.UtcNow.AddDays(-30).ToString("O")); command.ExecuteNonQuery();
    }
}
