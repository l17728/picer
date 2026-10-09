using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Vision;
using Picer.Core.Video;
using Picer.Core.Gateway;
using Picer.Core.Thumbnails;

namespace Picer.Tests;

public sealed class IncrementalDescriptionSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-description-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _db;
    private readonly long _source;
    public IncrementalDescriptionSearchTests()
    {
        _db = new PicerDatabase(Path.Combine(_root, "test.db"));
        _source = _db.AddSource(_root).Id;
    }
    private ImageRecord Add(string relative) => _db.UpsertImage(new ImageRecord
    {
        SourceId = _source, Path = Path.Combine(_root, relative), FileSize = 123,
        ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow, Status = ImageStatus.Active
    });

    [Fact]
    public void DirectoryPreviewEscapesWildcardsAndDoesNotWriteSharedDescriptionProjections()
    {
        var image = Add("photos_100%/one.jpg"); Add("photosX100more/two.jpg"); Add("photos_100%-old/three.jpg");
        image.Sha256 = new string('b', 64); _db.UpsertImage(image);
        var cloud = new CloudVisionRepository(_db);
        cloud.Save(image, new Analyzer(), "", "test", new("shared description", [], [], [], []));
        // A valid shared derivative with no physical result must be counted without writes.
        using (var removeTrigger = _db.Command("DROP TRIGGER content_remove_cloud_vision_analyses")) removeTrigger.ExecuteNonQuery();
        using (var remove = _db.Command("DELETE FROM cloud_vision_analyses WHERE image_id=$id"))
        { remove.Parameters.AddWithValue("$id", image.Id); remove.ExecuteNonQuery(); }
        using var changes = _db.Command("SELECT total_changes()"); var before = changes.ExecuteScalar();
        var plan = DirectoryDescriptionPlan.Build(_db, cloud, Path.Combine(_root, "photos_100%"));
        Assert.Equal(1, plan.Total); Assert.Equal(1, plan.AlreadyDescribed); Assert.Empty(plan.Pending);
        Assert.Equal(before, changes.ExecuteScalar());
        using var count = _db.Command("SELECT COUNT(*) FROM cloud_vision_analyses"); Assert.Equal(0L, count.ExecuteScalar());
        Assert.ThrowsAny<OperationCanceledException>(() => DirectoryMediaDescriptionPlan.Build(_db, cloud, new(_db), _root, new CancellationToken(true)));
    }

    [Fact]
    public void DirectoryTreeQueryReturnsDistinctFoldersInsteadOfEveryMediaPath()
    {
        Add("same/one.jpg"); Add("same/two.jpg"); Add("same/three.jpg");
        var nodes = _db.GetDirectoryTreePaths();
        Assert.Equal(2, nodes.Count); // one actual media directory and the configured source root
        Assert.All(nodes, path => Assert.EndsWith("/.directory-node", path));
    }

    [Fact]
    public void DirectoryPlanIncludesDescendantsButNotSiblingPrefixesAndReusesAnyCompletedModel()
    {
        var first = Add("photos/one.jpg");
        var nested = Add("photos/sub/two.jpg");
        Add("photos-old/three.jpg");
        var cloud = new CloudVisionRepository(_db);
        cloud.Save(first, new Analyzer(), "", "test", new("已有描述", [], [], [], []));
        var plan = DirectoryDescriptionPlan.Build(_db, cloud, Path.Combine(_root, "photos"));
        Assert.Equal(2, plan.Total);
        Assert.Equal(1, plan.AlreadyDescribed);
        Assert.Equal(nested.Id, Assert.Single(plan.Pending).Id);
        first.ModifiedUtc = first.ModifiedUtc.AddMinutes(1);
        Assert.False(cloud.HasCurrentDescription(first));
    }

    [Fact]
    public void ChineseBm25IndexUpdatesAndDeletesWithoutStaleResults()
    {
        var image = Add("photo.jpg");
        var repository = new MediaTextIndexRepository(_db);
        repository.UpsertImage(image, ["一个小孩穿着红色外套在公园玩耍"]);
        var search = new MediaTextRanking(_db);
        Assert.Contains(image.Id, search.Search(["红色外套"]).Keys);
        Assert.Contains(image.Id, new MediaSearchTool(_db).Execute(new(Terms: ["红色外套"])).Items.Select(x => x.Id));
        repository.UpsertImage(image, ["蓝色衣服"]);
        Assert.Empty(search.Search(["红色外套"]));
        Assert.Contains(image.Id, search.Search(["蓝色"]).Keys);
        using var delete = _db.Command("DELETE FROM media_text_documents WHERE media_id=$id");
        delete.Parameters.AddWithValue("$id", image.Id);
        delete.ExecuteNonQuery();
        Assert.Empty(search.Search(["蓝色"]));
    }

    [Fact]
    public void QueryEscapesFtsSyntaxAndRequiresWholeTerms()
    {
        var image = Add("photo.jpg");
        new MediaTextIndexRepository(_db).UpsertImage(image, ["红色外套"]);
        var search = new MediaTextRanking(_db);
        Assert.Empty(search.Search(["红色外套", "沙滩"], requireAll: true));
        Assert.Empty(search.Search(["\"红色外套\" OR *"]));
        Assert.Single(search.Search(["\"红色外套\""]));
    }

    [Fact]
    public void VideoPlanSkipsUnchangedSummaryAndRetainsFingerprintAcrossSync()
    {
        var repository = new VideoRepository(_db);
        var video = new VideoRecord(30, _source, Path.Combine(_root, "photos/sub/clip.mp4"), 987,
            DateTime.UtcNow, 5, 640, 480, null, null, "active");
        repository.UpsertMirrored(video, _source);
        var plan = DirectoryMediaDescriptionPlan.Build(_db, new(_db), repository, Path.Combine(_root, "photos"));
        Assert.Equal(video.Id, Assert.Single(plan.Videos).Id);
        repository.SaveSummary(video.Id, new Analyzer(), video, new("视频中的红色外套", [], [], [], []));
        var payload = _db.GetAllMirroredVideoSummaries();
        _db.UpsertMirroredVideoMetadata(new GatewayMetadata([], [], [], [], [], [], [], payload));
        Assert.True(repository.HasCurrentSummary(video));
        Assert.False(repository.HasCurrentSummary(video with { ModifiedUtc = video.ModifiedUtc.AddSeconds(1) }));
        Assert.Empty(DirectoryMediaDescriptionPlan.Build(_db, new(_db), repository, Path.Combine(_root, "photos")).Videos);
    }

    [Fact]
    public void VideoStructuredFiltersRequireConfirmedPeopleTagsConceptsAndLocation()
    {
        var repository = new VideoRepository(_db);
        var video = new VideoRecord(30, _source, Path.Combine(_root, "clip.mp4"), 987,
            DateTime.UtcNow, 5, 640, 480, null, "31.2,121.5", "active");
        repository.UpsertMirrored(video, _source);
        using (var tag = _db.Command("INSERT INTO tags(id,name,created_utc) VALUES(77,'旅行','2026-10-08T00:00:00Z')")) tag.ExecuteNonQuery();
        var metadata = new VideoSearchMetadata(_db);
        metadata.SetTag(video.Id, 77, true);
        metadata.SetConcept(video.Id, "landscape.mountain", "model-v1");
        var request = new MediaSearchRequest(MediaType: MediaTypeFilter.Video, TagIds: [77],
            VisualConceptIds: ["landscape.mountain"], Location: new("上海", 31, 32, 121, 122));
        var tool = new MediaSearchTool(_db, repository);
        Assert.Equal(video.Id, Assert.Single(tool.Execute(request).Items).Id);
        Assert.Empty(tool.Execute(request with { PersonIds = [999] }).Items);
        Assert.Empty(tool.Execute(request with { Location = new("其他", 0, 1, 0, 1) }).Items);
        metadata.SetConcept(video.Id, "landscape.mountain", "model-v1", "rejected");
        Assert.Empty(tool.Execute(request).Items);
    }

    [Fact]
    public void ContentThumbnailSurvivesDeletingOnePhysicalRecordsCache()
    {
        var thumbnails = new ThumbnailService(Path.Combine(_root, "thumbs"));
        var hash = new string('a', 64);
        var shared = thumbnails.GetCachePath(1, hash);
        Assert.Equal(shared, thumbnails.GetCachePath(2, hash.ToUpperInvariant()));
        File.WriteAllBytes(shared, [1, 2, 3]);
        thumbnails.Delete(1);
        Assert.True(File.Exists(shared));
        Assert.NotEqual(shared, new ThumbnailService(thumbnails.CacheDir, 512).GetCachePath(1, hash));
    }

    [Fact]
    public void ContentDerivativesSurvivePhysicalDeletionAndRestoreToAnotherCopy()
    {
        var source = Add("first.jpg");
        source.Sha256 = new string('b', 64);
        _db.UpsertContent(source.Sha256, source.FileSize, 0, 0);
        source.ContentSha256 = source.Sha256;
        _db.UpsertImage(source);
        var cloud = new CloudVisionRepository(_db);
        cloud.Save(source, new Analyzer(), "", "test", new("红色外套在海滩", [], [], [], []));
        using (var faces = _db.Command("""
            INSERT INTO detected_faces(image_id,model_version,ordinal,box_x,box_y,box_width,box_height,confidence,embedding,created_utc)
            VALUES($id,'model-a',0,0.1,0.2,0.3,0.4,0.99,X'01020304','2026-10-08T00:00:00Z');
            INSERT INTO detected_faces(image_id,model_version,ordinal,box_x,box_y,box_width,box_height,confidence,embedding,created_utc)
            VALUES($id,'model-b',0,0.1,0.2,0.3,0.4,0.99,X'05060708','2026-10-08T00:00:00Z');
            INSERT INTO face_review_states(face_id,state,updated_utc)
                SELECT id,'ignored','2026-10-08T00:01:00Z' FROM detected_faces WHERE image_id=$id AND model_version='model-a';
            INSERT INTO face_corrections(face_id,action,created_utc)
                SELECT id,'ignore','2026-10-08T00:01:00Z' FROM detected_faces WHERE image_id=$id AND model_version='model-a';
            """)) { faces.Parameters.AddWithValue("$id", source.Id); faces.ExecuteNonQuery(); }
        _db.DeleteImage(source.Id);
        var copy = _db.UpsertImage(new ImageRecord { SourceId = _source, Path = Path.Combine(_root, "copy.jpg"),
            FileSize = source.FileSize, ModifiedUtc = source.ModifiedUtc.AddMinutes(3), Sha256 = source.Sha256,
            ContentSha256 = source.Sha256, IndexedUtc = DateTime.UtcNow });
        Assert.True(cloud.HasCurrentDescription(copy));
        Assert.Equal("红色外套在海滩", cloud.GetLatestCompleted(copy.Id)?.Result.Summary);
        using var restored = _db.Command("SELECT model_version,hex(embedding) FROM detected_faces WHERE image_id=$id ORDER BY model_version");
        restored.Parameters.AddWithValue("$id", copy.Id);
        using var reader = restored.ExecuteReader();
        Assert.True(reader.Read()); Assert.Equal("model-a", reader.GetString(0)); Assert.Equal("01020304", reader.GetString(1));
        Assert.True(reader.Read()); Assert.Equal("model-b", reader.GetString(0)); Assert.Equal("05060708", reader.GetString(1));
        Assert.False(reader.Read()); reader.Close();
        Assert.Contains(copy.Id, new MediaTextRanking(_db).Search(["红色外套"]).Keys);
        Assert.Equal("ignored", Assert.Single(_db.GetFaceReviewDecisions()).State);
        _db.UpsertImage(copy); _db.UpsertImage(copy);
        using var corrections = _db.Command("SELECT COUNT(*) FROM face_corrections");
        Assert.Equal(1L, corrections.ExecuteScalar());
    }

    public void Dispose() { _db.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_root, recursive: true); }
    private sealed class Analyzer : ICloudVisionAnalyzer
    {
        public string Provider => "test";
        public string ModelVersion => "old";
        public string PromptVersion => "old";
        public Task<CloudVisionResult> AnalyzeAsync(CloudVisionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
