using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.People;

namespace Picer.Tests;

public sealed class FaceTrainingSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-curation-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _db;
    private long _faceId;
    private long _imageId;

    public FaceTrainingSnapshotTests()
    {
        Directory.CreateDirectory(_root);
        _db = new PicerDatabase(Path.Combine(_root, "picer.db"));
    }

    private long AddPerson(string name)
    {
        using var cmd = _db.Command("INSERT INTO people (display_name, created_utc, updated_utc) VALUES ($n, $t, $t); SELECT last_insert_rowid();");
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        return (long)cmd.ExecuteScalar()!;
    }

    private long AddImage(string fileName, string? sha256)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllBytes(path, [0x50, 0x49, 0x43]);
        var record = _db.UpsertImage(new ImageRecord
        {
            SourceId = _db.AddSource(_root).Id,
            Path = path,
            FileSize = 3,
            ModifiedUtc = DateTime.UtcNow,
            IndexedUtc = DateTime.UtcNow,
            Sha256 = sha256,
            Status = ImageStatus.Active
        });
        _imageId = record.Id;
        return record.Id;
    }

    private long AddFace(long imageId)
    {
        using var ordinalCmd = _db.Command("SELECT COALESCE(MAX(ordinal), -1) + 1 FROM detected_faces WHERE image_id = $i");
        ordinalCmd.Parameters.AddWithValue("$i", imageId);
        var ordinal = (long)ordinalCmd.ExecuteScalar()!;
        using var cmd = _db.Command("""
            INSERT INTO detected_faces (image_id, model_version, ordinal, box_x, box_y, box_width, box_height, confidence, created_utc)
            VALUES ($i, 'sface-test', $o, 0, 0, 1, 1, 0.99, $t); SELECT last_insert_rowid();
            """);
        cmd.Parameters.AddWithValue("$i", imageId);
        cmd.Parameters.AddWithValue("$o", ordinal);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        _faceId = (long)cmd.ExecuteScalar()!;
        return _faceId;
    }

    private void Confirm(long faceId, long personId)
    {
        using var cmd = _db.Command("""
            INSERT INTO face_person_assignments (face_id, person_id, state, confidence, assigned_by, updated_utc)
            VALUES ($f, $p, 'confirmed', 0.99, 'user', $t)
            ON CONFLICT (face_id) DO UPDATE SET person_id = $p, state = 'confirmed', updated_utc = $t
            """);
        cmd.Parameters.AddWithValue("$f", faceId);
        cmd.Parameters.AddWithValue("$p", personId);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private void Reject(long faceId, long personId)
    {
        using var cmd = _db.Command("""
            INSERT OR IGNORE INTO face_person_rejections (face_id, person_id, created_utc) VALUES ($f, $p, $t)
            """);
        cmd.Parameters.AddWithValue("$f", faceId);
        cmd.Parameters.AddWithValue("$p", personId);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private long AddLabeledFace(long imageId, long personId)
    {
        var faceId = AddFace(imageId);
        Confirm(faceId, personId);
        return faceId;
    }

    private string MemberSplit(long snapshotId, long faceId, long personId)
    {
        using var cmd = _db.Command("SELECT split FROM face_snapshot_members WHERE snapshot_id = $s AND face_id = $f AND person_id = $p");
        cmd.Parameters.AddWithValue("$s", snapshotId);
        cmd.Parameters.AddWithValue("$f", faceId);
        cmd.Parameters.AddWithValue("$p", personId);
        return Convert.ToString(cmd.ExecuteScalar())!;
    }

    [Fact]
    public void FacesOnSameImage_ShareLeakageGroupAndSplit()
    {
        var person = AddPerson("甲");
        var image = AddImage("a.png", null);
        var face1 = AddLabeledFace(image, person);
        var face2 = AddLabeledFace(image, person);

        var result = FaceTrainingSnapshotService.BuildSnapshot(_db);

        Assert.Equal(1, result.LeakageGroupCount);
        Assert.Equal(MemberSplit(result.SnapshotId, face1, person), MemberSplit(result.SnapshotId, face2, person));
    }

    [Fact]
    public void SameContentAcrossImages_SharesLeakageGroup()
    {
        var person = AddPerson("乙");
        var image1 = AddImage("a.png", "content-sha-1");
        var face1 = AddLabeledFace(image1, person);
        var image2 = AddImage("b.png", "content-sha-1");
        var face2 = AddLabeledFace(image2, person);

        var result = FaceTrainingSnapshotService.BuildSnapshot(_db);

        Assert.Equal(1, result.LeakageGroupCount);
        Assert.Equal(MemberSplit(result.SnapshotId, face1, person), MemberSplit(result.SnapshotId, face2, person));
    }

    [Fact]
    public void DistinctContent_DistinctLeakageGroups()
    {
        var person = AddPerson("丙");
        var face1 = AddLabeledFace(AddImage("a.png", "content-sha-a"), person);
        var face2 = AddLabeledFace(AddImage("b.png", "content-sha-b"), person);

        var result = FaceTrainingSnapshotService.BuildSnapshot(_db);

        Assert.Equal(2, result.LeakageGroupCount);
        Assert.NotEqual(MemberSplit(result.SnapshotId, face1, person) + ":" + 1,
            MemberSplit(result.SnapshotId, face2, person) + ":" + 2);
    }

    [Fact]
    public void DeterministicRebuild_ProducesSameManifestHash()
    {
        var person = AddPerson("丁");
        for (var i = 0; i < 6; i++)
            AddLabeledFace(AddImage($"d{i}.png", $"sha-d{i}"), person);

        var first = FaceTrainingSnapshotService.BuildSnapshot(_db, new FaceSnapshotRecipe(RandomSeed: 42));
        var second = FaceTrainingSnapshotService.BuildSnapshot(_db, new FaceSnapshotRecipe(RandomSeed: 42));

        Assert.Equal(first.ManifestHash, second.ManifestHash);
        Assert.Equal(first.MemberCount, second.MemberCount);
    }

    [Fact]
    public void NegativeFor_StoredWithFaceSplit()
    {
        var person = AddPerson("戊");
        var other = AddPerson("陌生人");
        var face = AddLabeledFace(AddImage("a.png", "content-sha-n"), person);
        Reject(face, other);

        var result = FaceTrainingSnapshotService.BuildSnapshot(_db);

        Assert.Equal(1, result.NegativePairCount);
        Assert.Equal(MemberSplit(result.SnapshotId, face, person),
            Convert.ToString(_db.Command($"SELECT split FROM face_snapshot_rejections WHERE snapshot_id = {result.SnapshotId} AND face_id = {face} AND person_id = {other}").ExecuteScalar()));
    }

    [Fact]
    public void CoverageBelowMinimum_ProducesWarnings()
    {
        var person = AddPerson("己");
        AddLabeledFace(AddImage("a.png", "content-sha-x"), person);
        AddLabeledFace(AddImage("b.png", "content-sha-y"), person);

        var result = FaceTrainingSnapshotService.BuildSnapshot(_db);
        var eligibility = FaceTrainingSnapshotService.CheckEligibility(_db);

        Assert.False(eligibility.MeetsMinimumQualification);
        Assert.Contains(result.Warnings, warning => warning.Contains("低于最低资格"));
    }

    [Fact]
    public void NoConfirmedFaces_Throws()
    {
        AddImage("a.png", null);
        Assert.Throws<InvalidOperationException>(() => FaceTrainingSnapshotService.BuildSnapshot(_db));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }
}
