using Picer.Core.Data;
using Picer.Core.Indexing;
using Picer.Core.Video;
using Picer.Core.Search;

namespace Picer.Tests;

public sealed class DirectoryMediaScannerTests
{
    [Fact]
    public async Task ScanDiscoversPhotosAndVideosRecursivelyAndImportsPagesWithoutDuplicates()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "videos"));
        Directory.CreateDirectory(Path.Combine(root, ".picer-keyframes"));
        await File.WriteAllBytesAsync(Path.Combine(root, "photo.jpg"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(root, "videos", "clip.mp4"), [4, 5, 6]);
        await File.WriteAllBytesAsync(Path.Combine(root, ".picer-keyframes", "frame.jpg"), [7]);
        try
        {
            using var db = new PicerDatabase(Path.Combine(root, "index.db"));
            var source = db.AddSource(root);
            var scanner = new DirectoryMediaScanner((_, _, _) => Task.FromResult(new DirectoryMediaMetadata(20, 10)));
            var request = new DirectoryMediaScanRequest(source.Id, root, Limit: 1);
            var first = await scanner.ReadAsync(request);
            Assert.Equal(2, first.Total); Assert.False(first.Done);
            DirectoryMediaImport.Apply(db, source.Id, root, first);
            DirectoryMediaImport.Apply(db, source.Id, root, first);
            var second = await scanner.ReadAsync(request with { Snapshot = first.Snapshot, Offset = first.NextOffset });
            Assert.True(second.Done);
            DirectoryMediaImport.Apply(db, source.Id, root, second);
            Assert.Single(db.GetImages()); Assert.Single(new VideoRepository(db).GetAll());
            Assert.Equal(64, db.GetImages()[0].Sha256!.Length);
            var indexed = db.GetImages()[0];
            using (var legacy = db.Command("""
                INSERT INTO cloud_vision_analyses(image_id,provider,model_version,prompt_version,source_file_size,source_modified_utc,location_fingerprint,status,summary,result_json,updated_utc)
                VALUES($id,'legacy','v1','v1',0,'','','completed','已有描述','{}','2026-10-08');
                """))
            { legacy.Parameters.AddWithValue("$id", indexed.Id); legacy.ExecuteNonQuery(); }
            var descriptions = new Picer.Core.Vision.CloudVisionRepository(db);
            Assert.False(descriptions.HasCurrentDescription(indexed));
            DirectoryMediaImport.Apply(db, source.Id, root, first);
            DirectoryMediaImport.Apply(db, source.Id, root, second);
            Assert.True(descriptions.HasCurrentDescription(indexed));
            Assert.Throws<UnauthorizedAccessException>(() => DirectoryMediaImport.Apply(db, source.Id, root + "-other", first));
            Assert.False(DirectoryMediaImport.Contains(root, root + "-other/photo.jpg"));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.ReadAsync(request, new CancellationToken(true)));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public void RawSyncedPhotoAndVideoDescriptionsImmediatelyParticipateInChineseBm25()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-sync-text-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var db = new PicerDatabase(Path.Combine(root, "index.db"));
            var source = db.AddSource(root);
            var image = db.UpsertImage(new() { SourceId = source.Id, Path = Path.Combine(root, "one.jpg"), IndexedUtc = DateTime.UtcNow, ModifiedUtc = DateTime.UtcNow });
            new VideoRepository(db).UpsertMirrored(new(42, source.Id, Path.Combine(root, "two.mp4"), 1, DateTime.UtcNow, null, 0, 0, null, null, "active"), source.Id);
            using var command = db.Command("""
                INSERT INTO cloud_vision_analyses(image_id,provider,model_version,prompt_version,source_file_size,source_modified_utc,location_fingerprint,status,summary,result_json,updated_utc)
                VALUES($id,'test','v1','v1',0,'','','completed','孩子在沙滩上玩耍','{}','2026-10-08');
                INSERT INTO video_summaries(video_id,source_file_size,source_modified_utc,provider,model_version,prompt_version,summary,result_json,created_utc)
                VALUES((SELECT content_id FROM videos WHERE id=42),1,'','test','v1','v1','孩子在沙滩上跑步','{}','2026-10-08');
                """);
            command.Parameters.AddWithValue("$id", image.Id); command.ExecuteNonQuery();
            var matches = new MediaTextRanking(db).Search(["沙滩"]);
            Assert.Contains(image.Id, matches.Keys); Assert.Contains(-42L, matches.Keys);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
