using System.Security.Cryptography;
using Picer.Core.Data;
using Picer.Core.Indexing;
using Picer.Core.Models;

namespace Picer.Tests;

public sealed class IndexRepairTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-repair-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _db;
    private readonly long _source;
    public IndexRepairTests()
    {
        _db = new PicerDatabase(Path.Combine(_root, "picer.db"));
        _source = _db.AddSource(Path.Combine(_root, "old")).Id;
    }

    private ImageRecord Reference(string name, byte value)
    {
        var hash = Convert.ToHexString(SHA256.HashData(new byte[] { value })).ToLowerInvariant();
        return _db.UpsertImage(new() { SourceId = _source, Path = Path.Combine(_root, "old", name), Sha256 = hash,
            FileSize = 1, ModifiedUtc = DateTime.UtcNow.AddDays(-1), IndexedUtc = DateTime.UtcNow, Camera = "preserved" });
    }

    private string Copy(string name, byte value)
    {
        var path = Path.Combine(_root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [value]); return path;
    }

    [Fact]
    public async Task MultipleRootsAndDuplicateCopiesPreserveIdsAndOneContentIdentity()
    {
        var first = Reference("a.jpg", 7); var second = Reference("b.jpg", 7);
        var absent = Reference("lost.jpg", 8);
        using (var description = _db.Command("""
            INSERT INTO cloud_vision_analyses(image_id,provider,model_version,prompt_version,source_file_size,source_modified_utc,location_fingerprint,status,summary,result_json,updated_utc)
            VALUES($id,'test','v1','v1',1,$modified,'','completed','keep description','{}',$modified)
            """))
        { description.Parameters.AddWithValue("$id", first.Id); description.Parameters.AddWithValue("$modified", first.ModifiedUtc.ToString("o")); description.ExecuteNonQuery(); }
        Copy("new1/a.jpg", 7); Copy("new2/b.jpg", 7); Copy("new2/deep/c.jpg", 7);
        var plan = await IndexRepairService.BuildAsync(_db.GetImages(), [Path.Combine(_root, "new1"), Path.Combine(_root, "new2"), Path.Combine(_root, "new2", "deep")]);
        Assert.Empty(plan.Failures);
        Assert.Equal(4, plan.Operations.Count);
        Assert.Single(plan.Operations.Where(operation => operation.AddReference));
        foreach (var operation in plan.Operations)
        {
            var evidence = await IndexRepairService.VerifyAsync(operation);
            var result = IndexRepairService.Apply(_db, operation, evidence);
            Assert.NotEqual("failed", result.Status);
        }
        Assert.Equal("preserved", _db.GetImage(first.Id)!.Camera);
        Assert.EndsWith("a.jpg", _db.GetImage(first.Id)!.Path);
        Assert.EndsWith("b.jpg", _db.GetImage(second.Id)!.Path);
        Assert.Equal(ImageStatus.Missing, _db.GetImage(absent.Id)!.Status);
        var counts = new ContentReferenceRepository(_db).GetCounts(first.Sha256!);
        Assert.Equal(3, counts.Available); Assert.Equal(0, counts.Missing);
        var descriptions = new Picer.Core.Vision.CloudVisionRepository(_db);
        foreach (var reference in new ContentReferenceRepository(_db).GetReferences(first.Sha256!)) Assert.True(descriptions.HasCurrentDescription(reference));
        Assert.Equal(2, _db.CountContentRows());
        var retry = await IndexRepairService.BuildAsync(_db.GetImages(), [Path.Combine(_root, "new1"), Path.Combine(_root, "new2")]);
        Assert.Single(retry.Operations); Assert.Null(retry.Operations[0].NewPath);
    }

    [Fact]
    public async Task ChangedTargetAndConcurrentIndexEditAreRejected()
    {
        var image = Reference("a.jpg", 7);
        var target = Copy("new/a.jpg", 7);
        var operation = Assert.Single((await IndexRepairService.BuildAsync(_db.GetImages(), [Path.Combine(_root, "new")])).Operations);
        File.WriteAllBytes(target, [9]);
        var result = IndexRepairService.Apply(_db, operation, await IndexRepairService.VerifyAsync(operation));
        Assert.Equal("hash_mismatch", result.Error);
        Assert.Equal(image.Path, _db.GetImage(image.Id)!.Path);
        File.WriteAllBytes(target, [7]);
        var evidence = await IndexRepairService.VerifyAsync(operation);
        using (var edit = _db.Command("UPDATE images SET file_size=999 WHERE id=$id"))
        { edit.Parameters.AddWithValue("$id", image.Id); edit.ExecuteNonQuery(); }
        Assert.Equal("index_changed_since_scan", IndexRepairService.Apply(_db, operation, evidence).Error);
    }

    [Fact]
    public async Task FewerNewCopiesNeverAssignOnePathTwiceOrRemoveUnmatchedReferences()
    {
        var first = Reference("a.jpg", 7); Reference("b.jpg", 7); Copy("new/a.jpg", 7);
        var plan = await IndexRepairService.BuildAsync(_db.GetImages(), [Path.Combine(_root, "new")]);
        foreach (var operation in plan.Operations) IndexRepairService.Apply(_db, operation, await IndexRepairService.VerifyAsync(operation));
        Assert.Equal(new ContentReferenceCounts(first.Sha256!, 1, 1), new ContentReferenceRepository(_db).GetCounts(first.Sha256!));
        Assert.Equal(2, _db.GetImages(includeMissing: true).Count);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IndexRepairService.BuildAsync(_db.GetImages(), [_root], token: new CancellationToken(true)));
    }

    public void Dispose()
    {
        _db.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_root, true);
    }
}
