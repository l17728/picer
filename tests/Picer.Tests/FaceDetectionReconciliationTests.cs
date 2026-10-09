using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class FaceDetectionReconciliationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-face-reconcile-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _database;
    private readonly ImageRecord _image;
    private readonly VisionAnalysisRepository _repository;
    private readonly Analyzer _analyzer = new();
    private static FaceDetection Face(float x) => new(new(x, .1f, .15f, .2f), .95f, Embedding: [1f, x]);

    public FaceDetectionReconciliationTests()
    {
        Directory.CreateDirectory(_root); _database = new PicerDatabase(Path.Combine(_root, "index.db"));
        _image = _database.UpsertImage(new ImageRecord { SourceId = _database.AddSource(_root).Id,
            Path = Path.Combine(_root, "image.jpg"), FileSize = 1, ModifiedUtc = DateTime.UtcNow,
            Status = ImageStatus.Active, Sha256 = new string('c', 64) });
        _repository = new VisionAnalysisRepository(_database);
    }

    [Fact]
    public void ReorderedDetectionsKeepIdsAndConfirmedIgnoredRejectedDecisions()
    {
        _repository.SaveCompleted(_image, _analyzer, new([Face(.1f), Face(.4f), Face(.7f)]));
        var ids = _repository.GetFaceIds(_image.Id).ToArray();
        var people = new PeopleService(_database); var person = people.Create("test-person");
        people.ConfirmFace(ids[0], person.Id); people.IgnoreFace(ids[1]);
        using (var reject = _database.Command("INSERT INTO face_person_rejections(face_id,person_id,created_utc) VALUES($face,$person,$now)"))
        { reject.Parameters.AddWithValue("$face", ids[2]); reject.Parameters.AddWithValue("$person", person.Id); reject.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o")); reject.ExecuteNonQuery(); }
        _repository.SaveCompleted(_image, _analyzer, new([Face(.7f), Face(.1f), Face(.4f)]));
        Assert.Equal(ids, _repository.GetFaceIds(_image.Id));
        Assert.Equal(ids[0], Assert.Single(_repository.GetConfirmedFaces(person.Id)).FaceId);
        Assert.Equal(ids[1], Assert.Single(_repository.GetIgnoredFaces()).FaceId);
        using var rejection = _database.Command("SELECT COUNT(*) FROM face_person_rejections");
        Assert.Equal(1L, rejection.ExecuteScalar());
    }

    [Fact]
    public void MissingReviewedFaceIsArchivedExcludedFromMatchingAndRestoredBySpatialIdentity()
    {
        _repository.SaveCompleted(_image, _analyzer, new([Face(.1f)]));
        var id = Assert.Single(_repository.GetFaceIds(_image.Id));
        var people = new PeopleService(_database); people.ConfirmFace(id, people.Create("test-person").Id);
        _repository.SaveCompleted(_image, _analyzer, new([Face(.7f)]));
        Assert.DoesNotContain(_repository.GetEmbeddingsForMatching(_analyzer.ModelVersion), face => face.FaceId == id);
        Assert.DoesNotContain(_repository.GetFaces(_image.Id), face => face.FaceId == id);
        Assert.True(_database.GetAllMirroredFaces().Single(face => face.Id == id).IsArchived);
        _repository.SaveCompleted(_image, _analyzer, new([Face(.1f)]));
        Assert.Equal(id, Assert.Single(_repository.GetFaces(_image.Id)).FaceId);
        Assert.False(_database.GetAllMirroredFaces().Single(face => face.Id == id).IsArchived);
    }

    [Fact]
    public void MidWriteFailureRollsBackUpdatedFacesAndManualState()
    {
        _repository.SaveCompleted(_image, _analyzer, new([Face(.1f)]));
        var id = Assert.Single(_repository.GetFaceIds(_image.Id));
        var people = new PeopleService(_database); people.IgnoreFace(id);
        using (var trigger = _database.Command("CREATE TRIGGER fail_new_face BEFORE INSERT ON detected_faces WHEN new.box_x>.6 BEGIN SELECT RAISE(ABORT,'injected_face_failure'); END")) trigger.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => _repository.SaveCompleted(_image, _analyzer, new([Face(.105f), Face(.7f)])));
        Assert.Equal(.1f, Assert.Single(_repository.GetFaces(_image.Id)).Box.X);
        Assert.Equal(id, Assert.Single(_repository.GetIgnoredFaces()).FaceId);
    }

    [Fact]
    public void AmbiguousSpatialMatchesDoNotTransferReviewedIdentity()
    {
        _repository.SaveCompleted(_image, _analyzer, new([Face(.1f)]));
        var id = Assert.Single(_repository.GetFaceIds(_image.Id));
        var people = new PeopleService(_database); people.ConfirmFace(id, people.Create("test-person").Id);
        // Both new boxes overlap the old box strongly but do not meet the deduplication threshold.
        _repository.SaveCompleted(_image, _analyzer, new([Face(.085f), Face(.115f)]));
        Assert.True(_database.GetAllMirroredFaces().Single(face => face.Id == id).IsArchived);
        Assert.All(_repository.GetFaces(_image.Id), face => Assert.Null(face.PersonId));
    }

    [Fact]
    public void DifferentModelResultIsRejectedBeforeMutation()
    {
        _repository.SaveCompleted(_image, _analyzer, new([Face(.1f)]));
        var id = Assert.Single(_repository.GetFaceIds(_image.Id));
        Assert.Throws<InvalidDataException>(() => _repository.SaveCompleted(_image, _analyzer,
            new([Face(.7f)]) { ModelVersion = "other-model" }));
        Assert.Equal(id, Assert.Single(_repository.GetFaceIds(_image.Id)));
    }

    [Fact]
    public void ArchivedStateSurvivesMirroringAndContentCopyRestore()
    {
        _repository.SaveCompleted(_image, _analyzer, new([Face(.1f)]));
        new PeopleService(_database).IgnoreFace(Assert.Single(_repository.GetFaceIds(_image.Id)));
        _repository.SaveCompleted(_image, _analyzer, new([]));
        var archived = Assert.Single(_database.GetAllMirroredFaces());
        Assert.True(archived.IsArchived);
        _database.UpsertMirroredFaces([archived], []);
        Assert.True(Assert.Single(_database.GetAllMirroredFaces()).IsArchived);
        var copy = _database.UpsertImage(new ImageRecord { SourceId = _image.SourceId, Path = Path.Combine(_root, "copy.jpg"),
            Sha256 = _image.Sha256, FileSize = _image.FileSize, ModifiedUtc = _image.ModifiedUtc, Status = ImageStatus.Active });
        Assert.True(_database.GetAllMirroredFaces().Single(face => face.ImageId == copy.Id).IsArchived);
        Assert.Empty(_repository.GetFaces(copy.Id));
    }

    public void Dispose() { _database.Dispose(); Directory.Delete(_root, true); }

    private sealed class Analyzer : IImageFaceAnalyzer
    {
        public string AnalyzerName => "reconcile-test";
        public string ModelVersion => "test-model-v1";
        public Task<FaceAnalysisResult> AnalyzeAsync(ImageRecord image, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
