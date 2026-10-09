using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Search;

namespace Picer.Tests;

public sealed class MetadataEditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-metadata-edits-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _db;
    public MetadataEditTests() { Directory.CreateDirectory(_root); _db = new(Path.Combine(_root, "index.db")); }

    [Fact]
    public void RenameAndAliasesSurviveOldSnapshotAndDatabaseReopen()
    {
        var people = new PeopleService(_db);
        var person = people.Create("旧名称");
        people.Rename(person.Id, "新名称");
        people.AddAlias(person.Id, "别名");
        people.SetDescription(person.Id, "本地描述");
        _db.UpsertMirroredPerson(new(person.Id, "旧名称", 0, 0));
        using var reopened = _db.OpenIndependentConnection();
        reopened.ReplayMetadataEdits();
        var actual = new PeopleService(reopened).Get(person.Id)!;
        Assert.Equal("新名称", actual.DisplayName);
        Assert.Contains("别名", actual.Aliases);
        Assert.Equal("本地描述", actual.Description);
        Assert.NotEmpty(reopened.GetMetadataEdits());
    }

    [Fact]
    public void ConflictingEditsConvergeRegardlessOfArrivalOrderAndDuplicateDelivery()
    {
        var person = new PeopleService(_db).Create("初始");
        var utc = DateTime.UtcNow.AddSeconds(1).ToString("O");
        var loser = new MetadataEdit("person", person.Id, "name", "客户端甲", utc, new string('1', 32));
        var winner = loser with { Value = "客户端乙", OperationId = new string('2', 32) };
        _db.MergeMetadataEdits([winner, loser, winner]);
        Assert.Equal("客户端乙", new PeopleService(_db).Get(person.Id)!.DisplayName);
        Assert.Equal(winner, Assert.Single(_db.MergeMetadataEdits([loser])));
        using var other = new PicerDatabase(Path.Combine(_root, "other.db"));
        other.UpsertMirroredPerson(new(person.Id, "初始", 0, 0));
        other.MergeMetadataEdits([loser, winner]);
        Assert.Equal("客户端乙", new PeopleService(other).Get(person.Id)!.DisplayName);
    }

    [Fact]
    public void RemovalTombstonesSurviveStaleTagAndAlbumLinks()
    {
        var source = _db.AddSource(_root);
        var image = _db.UpsertImage(new ImageRecord { SourceId = source.Id, Path = Path.Combine(_root, "photo.jpg"),
            ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow, Status = ImageStatus.Active });
        var tag = SemanticEngine.AddTag(_db, "标签");
        var album = SemanticEngine.AddAlbum(_db, "相册");
        SemanticEngine.AssignTag(_db, image.Id, tag.Id);
        SemanticEngine.AddToAlbum(_db, album.Id, image.Id);
        SemanticEngine.RemoveTag(_db, image.Id, tag.Id);
        SemanticEngine.RemoveFromAlbum(_db, album.Id, image.Id);
        _db.UpsertMirroredImageTag(image.Id, tag.Id);
        SemanticEngine.AddToAlbum(_db, album.Id, image.Id, recordEdit: false);
        _db.ReplayMetadataEdits();
        Assert.Empty(SemanticEngine.GetImageTags(_db, image.Id));
        Assert.Empty(SemanticEngine.GetAlbumImages(_db, album.Id));
        SemanticEngine.AssignTag(_db, image.Id, tag.Id);
        _db.ReplayMetadataEdits();
        Assert.Single(SemanticEngine.GetImageTags(_db, image.Id));
    }

    [Fact]
    public void UnknownEntityIsNotAcknowledgedAndInvalidBatchRollsBack()
    {
        var edit = new MetadataEdit("person", 999, "name", "待同步", DateTime.UtcNow.ToString("O"), Guid.NewGuid().ToString("N"));
        Assert.Empty(_db.MergeMetadataEdits([edit]));
        Assert.Empty(_db.GetMetadataEdits());
        Assert.Throws<ArgumentException>(() => _db.MergeMetadataEdits([edit with { Kind = "arbitrary_sql" }]));
    }

    public void Dispose() { _db.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_root, true); } catch (IOException) { } }
}
