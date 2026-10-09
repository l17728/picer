using Picer.Core.Data;
using Picer.Core.Models;

namespace Picer.Tests;

public sealed class ContentReferenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-references-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _db;
    private readonly long _source;
    private readonly string _hash = new('a', 64);

    public ContentReferenceTests()
    {
        _db = new PicerDatabase(Path.Combine(_root, "picer.db"));
        _source = _db.AddSource(_root).Id;
    }

    private ImageRecord Add(string name) => _db.UpsertImage(new()
    {
        SourceId = _source, Path = Path.Combine(_root, name), Sha256 = _hash,
        FileSize = 42, ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow
    });

    [Fact]
    public void CopiesHaveOneContentAndSeparateAvailableAndMissingCounts()
    {
        var first = Add("a/photo.jpg");
        var second = Add("b/photo.jpg");
        Add("b/photo.jpg"); // Retrying discovery never increments a separate counter.
        var references = new ContentReferenceRepository(_db);
        Assert.Equal(1, _db.CountContentRows());
        Assert.Equal(new ContentReferenceCounts(_hash, 2, 0), references.GetCounts(_hash.ToUpperInvariant()));
        _db.MarkMissing([first.Id]);
        Assert.Equal(new ContentReferenceCounts(_hash, 1, 1), references.GetCounts(_hash));
        Assert.Equal(2, references.GetReferences(_hash).Count);
        Assert.Equal(ImageStatus.Active, _db.GetImage(second.Id)!.Status);
    }

    [Fact]
    public async Task RemoteMirrorCannotRepublishDeletedOrRelocatedPaths()
    {
        Add("old/photo.jpg");
        var requests = new List<string>();
        using var client = new HttpClient(new CaptureHandler(requests)) { BaseAddress = new Uri("https://test.invalid/") };
        using var sync = new Picer.Core.Gateway.IndexSyncClient(client);
        var result = await sync.PushIncrementalAsync(_db, Path.Combine(_root,"checkpoint.json"), publishSceneLayouts: false, publishMedia: false);
        Assert.Equal(0, result.Images); Assert.Equal(0, result.Videos);
        Assert.DoesNotContain(requests, path => path.EndsWith("/images") || path.EndsWith("/videos"));
        Assert.Contains(requests, path => path.EndsWith("/metadata"));
    }

    private sealed class CaptureHandler(List<string> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent(request.RequestUri.AbsolutePath.EndsWith("metadata-edits") ? "[]" : "{}") });
        }
    }

    [Fact]
    public void DescriptionCreatedAfterCopiesWereIndexedIsReusedWithoutAnotherModelCall()
    {
        var first = Add("a/photo.jpg"); var second = Add("b/photo.jpg");
        using var command = _db.Command("""
            INSERT INTO cloud_vision_analyses(image_id,provider,model_version,prompt_version,source_file_size,source_modified_utc,location_fingerprint,status,summary,result_json,updated_utc)
            VALUES($id,'test','v1','v1',42,$modified,'','completed','shared description','{}',$modified)
            """);
        command.Parameters.AddWithValue("$id", first.Id); command.Parameters.AddWithValue("$modified", first.ModifiedUtc.ToString("o")); command.ExecuteNonQuery();
        Assert.True(new Picer.Core.Vision.CloudVisionRepository(_db).HasCurrentDescription(second));
        using var canonical = _db.Command("SELECT COUNT(*) FROM content_derivatives WHERE kind='cloud_vision_analyses'");
        Assert.Equal(1L, canonical.ExecuteScalar());
    }

    [Fact]
    public void ExplicitPurgeKeepsSharedContentUntilLastReferenceIsRemoved()
    {
        var first = Add("a/photo.jpg");
        using (var command = _db.Command("""
            INSERT INTO cloud_vision_analyses(image_id,provider,model_version,prompt_version,source_file_size,source_modified_utc,location_fingerprint,status,summary,result_json,updated_utc)
            VALUES($id,'test','v1','v1',42,$modified,'','completed','海边照片','{}',$modified)
            """))
        {
            command.Parameters.AddWithValue("$id", first.Id);
            command.Parameters.AddWithValue("$modified", first.ModifiedUtc.ToString("o"));
            command.ExecuteNonQuery();
        }
        var second = Add("b/photo.jpg");
        var thumbs = Path.Combine(_root, "thumbs"); Directory.CreateDirectory(thumbs);
        var sharedThumb = Path.Combine(thumbs, $"sha256-{_hash}.s256.v1.jpg");
        File.WriteAllBytes(sharedThumb, [1,2,3]);
        var assets = Path.Combine(_root,"remote-assets"); Directory.CreateDirectory(assets);
        var sharedAsset = Path.Combine(assets,_hash); File.WriteAllBytes(sharedAsset,[1,2,3]);
        var unrelated = Path.Combine(thumbs, "unrelated.part"); File.WriteAllText(unrelated, "keep");
        var purger = new ImageRecordPurger(_db);
        Assert.False(purger.Purge(first.Id));
        _db.MarkMissing([first.Id]);
        Assert.True(purger.Purge(first.Id));
        Assert.Null(_db.GetImage(first.Id));
        Assert.True(File.Exists(sharedThumb));
        Assert.True(File.Exists(sharedAsset));
        Assert.True(new Picer.Core.Vision.CloudVisionRepository(_db).HasCurrentDescription(_db.GetImage(second.Id)!));
        using (var canonical = _db.Command("SELECT COUNT(*) FROM content_derivatives WHERE kind='cloud_vision_analyses'"))
            Assert.Equal(1L, canonical.ExecuteScalar());
        _db.MarkMissing([second.Id]);
        Assert.True(purger.Purge(second.Id));
        Assert.True(purger.Purge(second.Id)); // Retry after remote deletion is harmless.
        Assert.Equal(0, _db.CountContentRows());
        Assert.False(File.Exists(sharedThumb));
        Assert.False(File.Exists(sharedAsset));
        Assert.True(File.Exists(unrelated));
        foreach (var table in new[] { "content_derivatives", "content_text_documents", "content_text_fts", "media_text_documents", "media_text_fts", "images_fts" })
        {
            using var command = _db.Command($"SELECT COUNT(*) FROM {table}");
            Assert.Equal(0L, command.ExecuteScalar());
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
