using System.IO;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Diagnostics;
using Picer.Core.Hashing;
using Picer.Core.Logging;
using Picer.Core.Metadata;
using Picer.Core.Models;
using Picer.Core.Storage;
using Picer.Core.Thumbnails;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Picer.Core.Indexing;

public sealed class IndexEngine
{
    private readonly PicerDatabase _db;
    private readonly IStorageProvider _storage;
    private readonly ThumbnailService _thumbnails;

    public IndexEngine(PicerDatabase db, IStorageProvider storage, ThumbnailService thumbnails)
    {
        _db = db;
        _storage = storage;
        _thumbnails = thumbnails;
    }

    public Task<IndexResult> IndexSourceAsync(
        SourceFolder source,
        IProgress<IndexProgress>? progress = null,
        CancellationToken ct = default) =>
        Task.Run(() => IndexSourceCoreAsync(source, progress, ct), ct);

    private async Task<IndexResult> IndexSourceCoreAsync(SourceFolder source, IProgress<IndexProgress>? progress, CancellationToken ct)
    {
        var dbSource = _db.AddSource(source.Path);
        AppLogger.Info($"开始索引源: {source.Path} (ID={dbSource.Id})");
        progress?.Report(new IndexProgress("扫描文件", 0, 0, source.Path));

        var files = (await _storage.EnumerateFilesAsync(source.Path, ct).ConfigureAwait(false))
            .Where(f => SupportedFormats.IsSupported(f.Key))
            .ToList();
        AppLogger.Info($"扫描到 {files.Count} 个支持格式的文件");

        var existing = _db.GetImages(dbSource.Id);
        var onDisk = new HashSet<string>(files.Select(f => f.Key), StringComparer.OrdinalIgnoreCase);

        var gone = existing.Where(e => e.Status == ImageStatus.Active && !onDisk.Contains(e.Path)).ToList();
        if (gone.Count > 0)
        {
            _db.MarkMissing(gone.Select(g => g.Id).ToList());
            AppLogger.Info($"标记 {gone.Count} 个缺失文件");
        }

        var byPath = existing.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);

        var work = new List<(StorageFileInfo File, ImageRecord? Existing)>();
        var skipped = 0;
        foreach (var file in files)
        {
            if (byPath.TryGetValue(file.Key, out var record) && record.Status == ImageStatus.Active &&
                record.FileSize == file.Size && record.ModifiedUtc == file.ModifiedUtc) skipped++;
            else work.Add((file, record));
        }

        var channel = Channel.CreateBounded<PreparedImage>(new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        var next = -1;
        var analyzed = 0;
        var workerCount = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
        {
            while (true)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= work.Count) break;
                ct.ThrowIfCancellationRequested();
                var item = work[index];
                try
                {
                    var prepared = await PrepareImageAsync(dbSource.Id, item.File, item.Existing, ct).ConfigureAwait(false);
                    await channel.Writer.WriteAsync(prepared, ct).ConfigureAwait(false);
                    var count = Interlocked.Increment(ref analyzed);
                    progress?.Report(new IndexProgress("分析元数据与内容", work.Count, count, item.File.Key));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLogger.Error($"索引文件失败: {item.File.Key}", ex);
                }
            }
        }, ct)).ToArray();
        var completion = Task.WhenAll(workers).ContinueWith(task =>
        {
            if (task.IsFaulted) channel.Writer.TryComplete(task.Exception?.InnerException);
            else if (task.IsCanceled) channel.Writer.TryComplete(new OperationCanceledException(ct));
            else channel.Writer.TryComplete();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        int indexed = 0, pendingWrites = 0;
        const int batchSize = 100;
        SqliteTransaction? tx = _db.BeginTransaction();

        try
        {
            await foreach (var prepared in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    PersistPreparedImage(prepared, tx);
                    indexed++;
                    pendingWrites++;
                    PicerEventSource.Log.FileIndexed();
                    progress?.Report(new IndexProgress("写入索引", work.Count, indexed, prepared.Record.Path));
                    if (pendingWrites >= batchSize)
                    {
                        tx.Commit();
                        tx.Dispose();
                        tx = _db.BeginTransaction();
                        pendingWrites = 0;
                    }
                }
                finally
                {
                    prepared.DecodedImage?.Dispose();
                }
            }
            tx.Commit();
            await completion.ConfigureAwait(false);
        }
        finally
        {
            tx?.Dispose();
        }
        progress?.Report(new IndexProgress("更新连拍照片组", 1, 0, source.Path));
        var burstGroups = new BurstGroupService(_db).RebuildSource(dbSource.Id);
        progress?.Report(new IndexProgress("更新连拍照片组", 1, 1, $"{burstGroups} 组"));
        AppLogger.Info($"索引完成: 扫描 {files.Count}, 新增 {indexed}, 跳过 {skipped}, 缺失 {gone.Count}");
        return new IndexResult(files.Count, indexed, skipped, gone.Count);
    }

    private async Task<PreparedImage> PrepareImageAsync(long sourceId, StorageFileInfo file, ImageRecord? existing, CancellationToken ct)
    {
        var normalizedPath = Uri.TryCreate(file.Key, UriKind.Absolute, out var uri) && !uri.IsFile ? file.Key : Path.GetFullPath(file.Key);

        await using var original = await _storage.OpenReadAsync(normalizedPath, ct).ConfigureAwait(false);
        await using var seekable = await EnsureSeekableAsync(original, ct).ConfigureAwait(false);
        using var stream = new CancellationReadStream(seekable, ct);

        ct.ThrowIfCancellationRequested();
        var sha = FileHasher.ComputeSha256(stream, ct);
        stream.Position = 0;

        if (existing is not null && existing.Sha256 == sha && existing.Status == ImageStatus.Active && existing.ThumbPath is not null)
        {
            existing.FileSize = file.Size;
            existing.ModifiedUtc = file.ModifiedUtc;
            existing.CreatedUtc = file.CreatedUtc ?? file.ModifiedUtc;
            existing.IndexedUtc = DateTime.UtcNow;
            return new PreparedImage(existing, null, false);
        }

        long aHash = 0, dHash = 0;
        int width = 0, height = 0;
        ImageMetadata meta = new();
        Image<Rgba32>? decodedImage = null;

        try
        {
            decodedImage = Image.Load<Rgba32>(stream);
            stream.Position = 0;

            meta = ImageMetadataReader.Read(stream);
            width = meta.Width;
            height = meta.Height;

            aHash = ComputeAverageHashFromImage(decodedImage);
            dHash = ComputeDifferenceHashFromImage(decodedImage);
        }
        catch { }
        finally
        {
        }

        var rec2 = existing ?? new ImageRecord();
        rec2.SourceId = sourceId;
        rec2.Path = normalizedPath;
        rec2.FileSize = file.Size;
        rec2.ModifiedUtc = file.ModifiedUtc;
        rec2.CreatedUtc = file.CreatedUtc ?? file.ModifiedUtc;
        rec2.Sha256 = sha;
        rec2.AverageHash = aHash;
        rec2.DifferenceHash = dHash;
        rec2.Width = width;
        rec2.Height = height;
        rec2.TakenUtc = meta.TakenUtc;
        rec2.Camera = meta.Camera;
        rec2.Gps = meta.Gps;
        rec2.Status = ImageStatus.Active;
        rec2.IndexedUtc = DateTime.UtcNow;

        return new PreparedImage(rec2, decodedImage, true);
    }

    private void PersistPreparedImage(PreparedImage prepared, SqliteTransaction tx)
    {
        var record = prepared.Record;
        void AttachThumbnail()
        {
            if (!prepared.NeedsThumbnail) return;
            record.ThumbPath = prepared.DecodedImage is not null
                ? _thumbnails.GetOrCreateFromImage(prepared.DecodedImage, record.Id)
                : _thumbnails.GetOrCreate(record.Path, record.Id);
        }

        if (!string.IsNullOrEmpty(record.Sha256))
        {
            _db.UpsertContentInTransaction(record.Sha256, record.FileSize, record.AverageHash, record.DifferenceHash, tx);
            var relocated = _db.FindRelocatableMissingRecordInTransaction(record.Sha256, record.Path, record.SourceId, tx);
            if (relocated is not null && !File.Exists(relocated.Path))
            {
                AppLogger.Info($"哈希命中，重建移动关联: ID={relocated.Id} {relocated.Path} -> {record.Path}");
                record.Id = relocated.Id;
                AttachThumbnail();
                _db.UpdateImageInTransaction(record, tx);
                return;
            }
        }

        _db.UpsertImageInTransaction(record, tx);
        AttachThumbnail();
        _db.UpdateImageInTransaction(record, tx);
    }

    private static async Task<Stream> EnsureSeekableAsync(Stream source, CancellationToken ct)
    {
        if (source.CanSeek) return new NonOwningStream(source);
        var path = Path.Combine(Path.GetTempPath(), $"picer-stream-{Guid.NewGuid():N}.tmp");
        var temp = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        await source.CopyToAsync(temp, ct).ConfigureAwait(false);
        temp.Position = 0;
        return temp;
    }

    private sealed record PreparedImage(ImageRecord Record, Image<Rgba32>? DecodedImage, bool NeedsThumbnail);

    private sealed class NonOwningStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => false;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush(); public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancellationReadStream(Stream inner, CancellationToken cancellationToken) : Stream
    {
        public override bool CanRead => inner.CanRead; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => false;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) { cancellationToken.ThrowIfCancellationRequested(); return inner.Read(buffer, offset, count); }
        public override int Read(Span<byte> buffer) { cancellationToken.ThrowIfCancellationRequested(); return inner.Read(buffer); }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static long ComputeAverageHashFromImage(Image<Rgba32> image)
    {
        var pixels = new int[64];
        using var tempImage = image.Clone(ctx => ctx.Resize(8, 8));

        long sum = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                var pixel = tempImage[x, y];
                int gray = (int)(0.2126f * pixel.R + 0.7152f * pixel.G + 0.0722f * pixel.B);
                pixels[y * 8 + x] = gray;
                sum += gray;
            }
        }

        int mean = (int)(sum / 64);
        long hash = 0;
        for (int i = 0; i < 64; i++)
        {
            if (pixels[i] > mean)
                hash |= 1L << i;
        }
        return hash;
    }

    private static long ComputeDifferenceHashFromImage(Image<Rgba32> image)
    {
        var comparisons = new bool[56];
        using var tempImage = image.Clone(ctx => ctx.Resize(9, 8));

        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                var left = GetGrayscale(tempImage[x, y]);
                var right = GetGrayscale(tempImage[x + 1, y]);
                comparisons[y * 8 + x] = left > right;
            }
        }

        long hash = 0;
        for (int i = 0; i < 56; i++)
        {
            if (comparisons[i])
                hash |= 1L << i;
        }
        return hash;
    }

    private static float GetGrayscale(Rgba32 p)
    {
        return 0.2126f * p.R + 0.7152f * p.G + 0.0722f * p.B;
    }

    public Task<List<ImageRecord>> VerifyAsync(CancellationToken ct = default) =>
        Task.Run(() =>
        {
            AppLogger.Info("开始验证文件是否存在...");
            var active = _db.GetImages(includeMissing: false);
            var missingIds = active
                .Where(a => !_storage.Exists(a.Path))
                .Select(a => a.Id)
                .ToList();
            _db.MarkMissing(missingIds);
            AppLogger.Info($"验证完成: {active.Count} 个文件, {missingIds.Count} 个缺失");
            return _db.GetMissingImages();
        }, ct);

    /// <summary>
    /// Removes only Picer's database record and generated thumbnail.
    /// This method never deletes, moves, or modifies the source image.
    /// </summary>
    public Task RemoveIndexRecordAsync(long id, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            _thumbnails.Delete(id);
            _db.DeleteImage(id);
        }, ct);

    public Task<RelocateResult> RelocateAsync(long id, string newPath, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var rec = _db.GetImage(id);
            if (rec is null)
                return RelocateResult.Failed(RelocateFailure.RecordNotFound);

            var info = _storage.GetInfo(newPath);
            if (info is null || !SupportedFormats.IsSupported(info.Key))
                return RelocateResult.Failed(RelocateFailure.UnsupportedOrMissingFile);

            if (string.IsNullOrWhiteSpace(rec.Sha256))
                return RelocateResult.Failed(RelocateFailure.OriginalHashUnavailable);

            string candidateHash;
            try
            {
                ct.ThrowIfCancellationRequested();
                using var stream = _storage.OpenRead(newPath);
                candidateHash = FileHasher.ComputeSha256(stream);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"无法校验重新定位文件: {newPath}", ex);
                return RelocateResult.Failed(RelocateFailure.FileReadFailed);
            }

            if (!string.Equals(rec.Sha256, candidateHash, StringComparison.OrdinalIgnoreCase))
                return RelocateResult.Failed(RelocateFailure.HashMismatch);

            rec.Path = Path.GetFullPath(newPath);
            rec.ModifiedUtc = info.ModifiedUtc;
            rec.FileSize = info.Size;
            rec.Status = ImageStatus.Active;
            rec.IndexedUtc = DateTime.UtcNow;
            using var tx = _db.BeginTransaction();
            _db.UpdateImageInTransaction(rec, tx);
            tx.Commit();
            return RelocateResult.Succeeded(rec);
        }, ct);

    public Task<RelocationPlan> BuildRelocationPlanAsync(string newRoot, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var missing = _db.GetMissingImages().Where(image => !string.IsNullOrWhiteSpace(image.Sha256)).ToList();
            var candidates = _storage.EnumerateFiles(newRoot)
                .Where(file => SupportedFormats.IsSupported(file.Key))
                .ToList();
            var byHash = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var stream = _storage.OpenRead(candidate.Key);
                    var hash = FileHasher.ComputeSha256(stream);
                    if (!byHash.TryGetValue(hash, out var paths)) byHash[hash] = paths = [];
                    paths.Add(candidate.Key);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLogger.Error($"批量重新定位时无法读取: {candidate.Key}", ex);
                }
            }

            var matches = new List<RelocationMatch>();
            var conflicts = new List<RelocationConflict>();
            var unmatched = new List<long>();
            foreach (var image in missing)
            {
                if (!byHash.TryGetValue(image.Sha256!, out var paths)) unmatched.Add(image.Id);
                else if (paths.Count == 1) matches.Add(new(image.Id, image.Path, paths[0]));
                else conflicts.Add(new(image.Id, image.Path, paths.AsReadOnly()));
            }
            return new RelocationPlan(Guid.NewGuid(), matches.AsReadOnly(), conflicts.AsReadOnly(), unmatched.AsReadOnly());
        }, ct);

    public async Task<IReadOnlyList<RelocateResult>> ApplyRelocationPlanAsync(RelocationPlan plan, CancellationToken ct = default)
    {
        var results = new List<RelocateResult>(plan.Matches.Count);
        foreach (var match in plan.Matches)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await RelocateAsync(match.ImageId, match.NewPath, ct).ConfigureAwait(false));
        }
        return results;
    }
}

public enum RelocateFailure
{
    None,
    RecordNotFound,
    UnsupportedOrMissingFile,
    OriginalHashUnavailable,
    FileReadFailed,
    HashMismatch
}

public sealed record RelocateResult(ImageRecord? Image, RelocateFailure Failure)
{
    public bool Success => Image is not null && Failure == RelocateFailure.None;

    public static RelocateResult Succeeded(ImageRecord image) => new(image, RelocateFailure.None);

    public static RelocateResult Failed(RelocateFailure failure) => new(null, failure);
}

public sealed record RelocationMatch(long ImageId, string OldPath, string NewPath);
public sealed record RelocationConflict(long ImageId, string OldPath, IReadOnlyList<string> CandidatePaths);
public sealed record RelocationPlan(
    Guid PlanId,
    IReadOnlyList<RelocationMatch> Matches,
    IReadOnlyList<RelocationConflict> Conflicts,
    IReadOnlyList<long> UnmatchedImageIds);
