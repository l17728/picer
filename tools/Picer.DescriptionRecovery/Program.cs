using System.Text.Json;
using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Video;
using Picer.Core.Vision;

if (args.Length == 4 && args[0] == "--latency")
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    await IndexReadLatencyProbe.RunAsync(args[1], args[2], args[3]);
    return;
}

if (args.Length == 4 && args[0] == "--audit")
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    await IndexUnionAudit.RunAsync(args[1], args[2], args[3]);
    return;
}

// Usage: database captured-results.jsonl expected-mirror-epoch --dry-run|--apply
// Apply requires the GUI to be closed. PicerDatabase enforces the migration lifetime lease and backup.
if (args.Length != 4 || args[3] is not ("--dry-run" or "--apply") || !Guid.TryParseExact(args[2], "N", out _))
    throw new ArgumentException("expected_database_capture_epoch_and_mode");
var path = Path.GetFullPath(args[0]);
var apply = args[3] == "--apply";
var eligible = new List<(long CaptureId, GatewayCloudVisionAnalysis? Photo, GatewayVideoSummary? Video)>();
var rejected = 0;
using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
{
    connection.Open();
    using var epoch = connection.CreateCommand(); epoch.CommandText = "SELECT epoch FROM mirror_checkpoint WHERE id=1";
    if (epoch.ExecuteScalar() as string != args[2]) throw new InvalidOperationException("recovery_epoch_mismatch");
    foreach (var line in File.ReadLines(args[1]))
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement; var row = root.GetProperty("row");
        string Text(string key) => row.GetProperty(key).GetString() ?? throw new InvalidDataException("missing_capture_field");
        long Number(string key) => row.GetProperty(key).GetInt64();
        var isPhoto = root.GetProperty("table").GetString() == "cloud_vision_analyses";
        if (!isPhoto && root.GetProperty("table").GetString() != "video_summaries") { rejected++; continue; }
        var id = Number(isPhoto ? "image_id" : "video_id");
        var size = Number("source_file_size"); var modified = Text("source_modified_utc");
        using var reference = connection.CreateCommand();
        reference.CommandText = isPhoto
            ? "SELECT id FROM images WHERE id=$id AND file_size=$size AND modified_utc=$mtime"
            : "SELECT id FROM videos WHERE content_id=$id AND file_size=$size AND modified_utc=$mtime ORDER BY id LIMIT 1";
        reference.Parameters.AddWithValue("$id", id); reference.Parameters.AddWithValue("$size", size); reference.Parameters.AddWithValue("$mtime", modified);
        if (reference.ExecuteScalar() is not long referenceId) { rejected++; continue; }
        var summary = Text("summary"); var resultJson = Text("result_json");
        if (JsonSerializer.Deserialize<CloudVisionResult>(resultJson)?.Summary != summary) { rejected++; continue; }
        if (isPhoto)
            eligible.Add((id, new(referenceId, Text("provider"), Text("model_version"), Text("prompt_version"), "completed", summary,
                resultJson, Text("updated_utc"), size, modified), null));
        else
            eligible.Add((id, null, new(referenceId, Text("provider"), Text("model_version"), Text("prompt_version"), summary, resultJson,
                size, modified) { UpdatedUtc = Text("created_utc") }));
    }
}
var capturedVersions = eligible.Count;
// Preserve distinct versions; the index chooses the same active version on both sides.
eligible = eligible.Distinct().ToList();
var eligibleMedia = eligible.Select(item => (item.Photo is null ? "video" : "image", item.CaptureId)).Distinct().Count();
if (apply)
{
    using var db = new PicerDatabase(path) { EnableDescriptionSync = true };
    if (db.GetMirrorCheckpoint()?.Epoch != args[2]) throw new InvalidOperationException("recovery_epoch_mismatch");
    using var transaction = db.BeginTransaction();
    foreach (var item in eligible)
    {
        if (item.Photo is { } candidatePhoto)
        {
            var image = db.GetImage(candidatePhoto.ImageId);
            if (image is null || image.FileSize != candidatePhoto.SourceFileSize || image.ModifiedUtc.ToString("o") != candidatePhoto.SourceModifiedUtc)
            { rejected++; continue; }
        }
        if (item.Video is { } candidateVideo)
        {
            var currentVideo = new VideoRepository(db).Get(candidateVideo.VideoId);
            if (currentVideo is null || currentVideo.FileSize != candidateVideo.SourceFileSize || currentVideo.ModifiedUtc.ToString("o") != candidateVideo.SourceModifiedUtc)
            { rejected++; continue; }
        }
        db.QueueRecoveredDescription(item.Photo, item.Video);
        db.UpsertMirroredVideoMetadata(new GatewayMetadata([], [], [], [], [], [], [], item.Video is null ? [] : [item.Video],
            CloudVisionAnalyses: item.Photo is null ? [] : [item.Photo]), externalTransaction: transaction);
    }
    transaction.Commit();
    Console.WriteLine($"queued={db.GetDescriptionSyncCounts().Pending}");
}
Console.WriteLine($"mode={(apply ? "apply" : "dry-run")}; capturedVersions={capturedVersions}; eligibleVersions={eligible.Count}; eligibleMedia={eligibleMedia}; rejected={rejected}; modelCalls=0");
