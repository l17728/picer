using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Search;

namespace Picer.Tests;

public sealed class TagAlbumSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-tag-album-sync-" + Guid.NewGuid().ToString("N"));

    private PicerDatabase CreateDatabase(out ImageRecord one, out ImageRecord two)
    {
        var database = new PicerDatabase(Path.Combine(_root, "picer.db"));
        var source = database.AddSource(@"C:\Photos");
        ImageRecord Record(string name) => database.UpsertImage(new ImageRecord
        {
            SourceId = source.Id, Path = $@"C:\Photos\{name}", FileSize = 10,
            ModifiedUtc = DateTime.UnixEpoch, IndexedUtc = DateTime.UtcNow, Status = ImageStatus.Active
        });
        one = Record("one.jpg");
        two = Record("two.jpg");
        return database;
    }

    [Fact]
    public void AddTagWithAlbumCreatesTagMarkerAlbumAndMirrorsMembership()
    {
        using var database = CreateDatabase(out var one, out var two);
        var (tag, album) = SemanticEngine.AddTagWithAlbum(database, "海滨之行", [one.Id, two.Id]);

        Assert.Equal("海滨之行", tag.Name);
        Assert.Equal("海滨之行", album.Name);
        Assert.Equal(SemanticEngine.TagAlbumDescription, album.Description);
        Assert.Equal(tag.Id, Assert.Single(SemanticEngine.GetImageTags(database, one.Id)).Id);
        Assert.Equal(2, SemanticEngine.GetAlbumImageCount(database, album.Id));

        // Reusing the same name adds neither a duplicate tag nor a duplicate album.
        var (reusedTag, reusedAlbum) = SemanticEngine.AddTagWithAlbum(database, "海滨之行", [two.Id]);
        Assert.Equal(tag.Id, reusedTag.Id);
        Assert.Equal(album.Id, reusedAlbum.Id);
        Assert.Single(SemanticEngine.GetTags(database));
        Assert.Equal(2, SemanticEngine.GetAlbumImageCount(database, album.Id));
    }

    [Fact]
    public void AddTagWithAlbumReusesManualSameNamedAlbumWithoutRetaggingIt()
    {
        using var database = CreateDatabase(out var one, out _);
        var manual = SemanticEngine.AddAlbum(database, "旅行");

        var (_, album) = SemanticEngine.AddTagWithAlbum(database, "旅行", [one.Id]);

        Assert.Equal(manual.Id, album.Id);
        Assert.Null(album.Description);
        Assert.Equal(1, SemanticEngine.GetAlbumImageCount(database, manual.Id));
    }

    [Fact]
    public void DeleteTagWithLinkedAlbumRemovesMarkerAlbumButKeepsManualAlbum()
    {
        using var database = CreateDatabase(out var one, out _);
        var (linked, _) = SemanticEngine.AddTagWithAlbum(database, "自动同步标签", [one.Id]);
        var manualTag = SemanticEngine.AddTag(database, "手动同名");
        var manualAlbum = SemanticEngine.AddAlbum(database, "手动同名");

        Assert.True(SemanticEngine.DeleteTagWithLinkedAlbum(database, linked.Id));
        Assert.Empty(SemanticEngine.GetTags(database).Where(item => item.Id == linked.Id));
        Assert.Empty(SemanticEngine.GetAlbums(database).Where(item => item.Name == "自动同步标签"));

        // A same-named manual album is not a linked tag album: the tag goes, the album stays.
        Assert.False(SemanticEngine.DeleteTagWithLinkedAlbum(database, manualTag.Id));
        Assert.Empty(SemanticEngine.GetTags(database).Where(item => item.Id == manualTag.Id));
        Assert.Contains(SemanticEngine.GetAlbums(database), item => item.Id == manualAlbum.Id);
    }

    [Fact]
    public void BackfillCreatesTagAlbumsWithMembershipAndReconcilesCameraTags()
    {
        using var database = CreateDatabase(out var one, out var two);
        var travel = SemanticEngine.AddTag(database, "旅行");
        SemanticEngine.AssignTag(database, one.Id, travel.Id);
        SemanticEngine.AssignTag(database, two.Id, travel.Id);
        var cameraAlbum = SemanticEngine.AddAlbum(database, "相机: Canon", "按相机品牌自动创建");
        var canon = SemanticEngine.AddTag(database, "canon");
        SemanticEngine.AssignTag(database, one.Id, canon.Id);

        var created = SemanticEngine.BackfillTagAlbums(database);

        Assert.Equal(1, created);
        var albums = SemanticEngine.GetAlbums(database);
        var travelAlbum = Assert.Single(albums, item => item.Name == "旅行");
        Assert.Equal(SemanticEngine.TagAlbumDescription, travelAlbum.Description);
        Assert.Equal(2, SemanticEngine.GetAlbumImageCount(database, travelAlbum.Id));
        // The camera tag reconciles with its existing "相机: Canon" album instead of duplicating.
        Assert.DoesNotContain(albums, item => string.Equals(item.Name, "canon", StringComparison.Ordinal));
        Assert.Equal(1, SemanticEngine.GetAlbumImageCount(database, cameraAlbum.Id));
        // Repeated runs are incremental no-ops.
        Assert.Equal(0, SemanticEngine.BackfillTagAlbums(database));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
