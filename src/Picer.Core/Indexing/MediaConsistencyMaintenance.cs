using System.Text.Json;
using Picer.Core.Data;
using Picer.Core.Video;

namespace Picer.Core.Indexing;

/// <summary>Keyset-driven maintenance of known references; checkpoints contain only numeric cursors.</summary>
public sealed class MediaConsistencyMaintenance
{
    public sealed record Checkpoint(long Photo = 0, long Video = 0, long PhotoAudit = 0, long VideoAudit = 0, bool NextVideo = false);
    public sealed record PassResult(int Checked, int Changed, int Unknown, bool Deferred);
    private readonly Func<PicerDatabase> _openDatabase;
    private readonly Func<MediaConsistencyRequest, CancellationToken, Task<MediaConsistencyBatch?>> _verify;
    private readonly string? _checkpointPath;
    private readonly int _batchSize;
    private readonly SemaphoreSlim _run = new(1, 1);
    private Checkpoint _checkpoint;

    public MediaConsistencyMaintenance(Func<PicerDatabase> openDatabase,
        Func<MediaConsistencyRequest, CancellationToken, Task<MediaConsistencyBatch?>> verify,
        string? checkpointPath = null, int batchSize = 32)
    {
        if (batchSize is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(batchSize));
        _openDatabase = openDatabase; _verify = verify; _checkpointPath = checkpointPath; _batchSize = batchSize;
        try { _checkpoint = checkpointPath is not null && File.Exists(checkpointPath)
            ? JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(checkpointPath)) ?? new() : new(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { _checkpoint = new(); }
    }

    internal static MediaConsistencyCandidate? ReadCandidate(PicerDatabase db, bool video, long id)
    {
        if (video)
        {
            var record = new VideoRepository(db).Get(id);
            var source = record is null ? null : db.GetSource(record.SourceId);
            return record is null || source is null ? null : new(record.Id, record.SourceId, source.Path, record.Path,
                record.FileSize, record.ModifiedUtc, record.Sha256, true);
        }
        var image = db.GetImage(id);
        var imageSource = image is null ? null : db.GetSource(image.SourceId);
        return image is null || imageSource is null ? null : new(image.Id, image.SourceId, imageSource.Path, image.Path,
            image.FileSize, image.ModifiedUtc, image.ContentSha256 ?? image.Sha256, false);
    }

    private static MediaConsistencyCandidate[] ReadNext(PicerDatabase db, bool video, long cursor, int limit)
    {
        using var command = db.Command($"SELECT id FROM {(video ? "videos" : "images")} WHERE id>$after ORDER BY id LIMIT $limit");
        command.Parameters.AddWithValue("$after", Math.Max(0, cursor)); command.Parameters.AddWithValue("$limit", limit);
        var ids = new List<long>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids.Select(id => ReadCandidate(db, video, id)).OfType<MediaConsistencyCandidate>().ToArray();
    }

    public async Task<PassResult> RunOnceAsync(CancellationToken token)
    {
        await _run.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var video = _checkpoint.NextVideo;
            MediaConsistencyCandidate[] batch;
            MediaConsistencyCandidate? audit;
            using (var db = _openDatabase())
            {
                batch = ReadNext(db, video, video ? _checkpoint.Video : _checkpoint.Photo, _batchSize);
                if (batch.Length == 0) batch = ReadNext(db, video, 0, _batchSize);
                audit = ReadNext(db, video, video ? _checkpoint.VideoAudit : _checkpoint.PhotoAudit, 1).FirstOrDefault()
                    ?? ReadNext(db, video, 0, 1).FirstOrDefault();
            }
            _checkpoint = _checkpoint with { NextVideo = !video };
            if (batch.Length == 0) { SaveCheckpoint(); return new(0, 0, 0, false); }
            var items = batch.ToList();
            var auditIndex = audit is null ? -1 : items.FindIndex(item => item.Id == audit.Id);
            if (audit is not null && auditIndex < 0) { auditIndex = items.Count; items.Add(audit); }
            var response = await _verify(new(items, auditIndex), token).ConfigureAwait(false);
            if (response is null) return new(0, 0, 0, true);
            var expected = items.ToDictionary(item => (item.Id, item.IsVideo));
            var results = new Dictionary<(long, bool), MediaConsistencyResult>();
            foreach (var result in response.Items)
                if (!expected.ContainsKey((result.Id, result.IsVideo)) || !results.TryAdd((result.Id, result.IsVideo), result))
                    throw new InvalidDataException("maintenance_response_outside_request");
            var changed = 0;
            using (var db = _openDatabase())
                foreach (var result in results.Values)
                {
                    token.ThrowIfCancellationRequested();
                    if (DirectoryMediaImport.ApplyVerifiedExisting(db, expected[(result.Id, result.IsVideo)], result)) changed++;
                }
            var last = batch.TakeWhile(item => results.ContainsKey((item.Id, item.IsVideo))).LastOrDefault();
            var audited = audit is not null && results.ContainsKey((audit.Id, audit.IsVideo));
            _checkpoint = video
                ? _checkpoint with { Video = last?.Id ?? _checkpoint.Video, VideoAudit = audited ? audit!.Id : _checkpoint.VideoAudit }
                : _checkpoint with { Photo = last?.Id ?? _checkpoint.Photo, PhotoAudit = audited ? audit!.Id : _checkpoint.PhotoAudit };
            SaveCheckpoint();
            return new(results.Count, changed, results.Values.Count(item => item.State == "unknown"), response.Deferred);
        }
        finally { _run.Release(); }
    }

    private void SaveCheckpoint()
    {
        if (_checkpointPath is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_checkpointPath))!);
        var temporary = _checkpointPath + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(_checkpoint)); File.Move(temporary, _checkpointPath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
