using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Png;
using Picer.Core.Data;
using Picer.Core.Indexing;
using Picer.Core.Models;
using Picer.Core.Storage;
using Picer.Core.Thumbnails;

namespace Picer.Tests;

public sealed class ContentReassociationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-content-" + Guid.NewGuid().ToString("N"));
    private readonly string _thumbsRoot = Path.Combine(Path.GetTempPath(), "picer-content-thumbs-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _db;
    private readonly IndexEngine _engine;
    private readonly long _sourceId;

    public ContentReassociationTests()
    {
        Directory.CreateDirectory(_root);
        _db = new PicerDatabase(Path.Combine(_root, "picer.db"));
        _engine = new IndexEngine(_db, new LocalStorageProvider(), new ThumbnailService(_thumbsRoot));
        _sourceId = _db.AddSource(_root).Id;
    }

    private string WriteFile(string relativePath, byte[] bytes)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] ImageBytes(byte seed)
    {
        using var image = new Image<Rgb24>(2, 1, new Rgb24(seed, (byte)(seed + 1), (byte)(seed + 2)));
        using var encoded = new MemoryStream();
        image.Save(encoded, new PngEncoder());
        return encoded.ToArray();
    }

    private async Task<List<ImageRecord>> IndexOnceAsync()
    {
        await _engine.IndexSourceAsync(new SourceFolder { Id = _sourceId, Path = _root });
        return _db.GetImages(_sourceId, includeMissing: true);
    }

    [Fact]
    public async Task MovedFile_KeepsRecordIdThumbnailAndBecomesActive()
    {
        var path1 = WriteFile("a.jpg", ImageBytes(1));
        var records = await IndexOnceAsync();
        var original = Assert.Single(records);
        Assert.Equal(path1, original.Path);
        Assert.Equal(ImageStatus.Active, original.Status);
        Assert.False(string.IsNullOrEmpty(original.ThumbPath));
        Assert.True(File.Exists(original.ThumbPath));

        var path2 = Path.Combine(_root, "b.jpg");
        File.Move(path1, path2);
        records = await IndexOnceAsync();

        var moved = Assert.Single(records);
        Assert.Equal(original.Id, moved.Id);
        Assert.Equal(path2, moved.Path);
        Assert.Equal(ImageStatus.Active, moved.Status);
        Assert.False(string.IsNullOrEmpty(moved.ThumbPath));
        Assert.True(File.Exists(moved.ThumbPath));
        Assert.Equal(1L, _db.CountContentRows());
    }

    [Fact]
    public async Task CopiedFile_CreatesDistinctInstancesSharingOneContentRow()
    {
        WriteFile("a.jpg", ImageBytes(7));
        WriteFile("b.jpg", ImageBytes(7));
        var records = await IndexOnceAsync();

        Assert.Equal(2, records.Count);
        Assert.NotEqual(records[0].Id, records[1].Id);
        Assert.NotNull(records[0].Sha256);
        Assert.Equal(records[0].Sha256, records[1].Sha256);
        Assert.Equal(records[0].ContentSha256, records[1].ContentSha256);
        Assert.Equal(1L, _db.CountContentRows());
        Assert.Equal("complete", _db.GetContentHashState(records[0].Sha256!));
    }

    [Fact]
    public async Task DistinctContents_ProduceDistinctContentRows()
    {
        WriteFile("a.jpg", ImageBytes(1));
        WriteFile("b.jpg", ImageBytes(2));
        var records = await IndexOnceAsync();

        Assert.Equal(2, records.Count);
        Assert.NotEqual(records[0].Sha256, records[1].Sha256);
        Assert.Equal(2L, _db.CountContentRows());
    }

    [Fact]
    public async Task ReindexUnchangedFile_KeepsRecordId()
    {
        WriteFile("a.jpg", ImageBytes(3));
        var first = Assert.Single(await IndexOnceAsync());
        var second = Assert.Single(await IndexOnceAsync());
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1L, _db.CountContentRows());
    }

    [Fact]
    public void UpsertImage_WithoutSha_LeavesContentUnlinked()
    {
        var path = WriteFile("raw.dat", [0x01]);
        _db.UpsertImage(new ImageRecord { SourceId = _sourceId, Path = path, FileSize = 1, ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow, Status = ImageStatus.Active });
        Assert.Equal(0L, _db.CountContentRows());
        using var cmd = _db.Command("SELECT content_sha256 FROM images WHERE path = $p");
        cmd.Parameters.AddWithValue("$p", path);
        Assert.Equal(DBNull.Value, cmd.ExecuteScalar());
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, true); } catch { }
        try { Directory.Delete(_thumbsRoot, true); } catch { }
    }
}
