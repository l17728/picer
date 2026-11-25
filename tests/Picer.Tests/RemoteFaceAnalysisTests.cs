using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Vision;

namespace Picer.Tests;

/// <summary>
/// Remote (photo space) mode runs face detection on the index service. The service stores the
/// faces; the GUI mirror only records that a photo was analyzed and receives faces through
/// synchronization. These tests pin the repository pieces that path relies on.
/// </summary>
public sealed class RemoteFaceAnalysisTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-remote-face-" + Guid.NewGuid().ToString("N"));

    private sealed class FixedAnalyzer(string model) : IImageFaceAnalyzer
    {
        public string AnalyzerName => "index-service-face";
        public string ModelVersion => model;
        public Task<FaceAnalysisResult> AnalyzeAsync(ImageRecord image, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The analyzer is not invoked by these tests.");
    }

    private static FaceAnalysisResult OneFace() =>
        new([new FaceDetection(new NormalizedFaceBox(.2f, .2f, .3f, .3f), .95f)]);

    private ImageRecord AddImage(PicerDatabase database, long sourceId, string name) =>
        database.UpsertImage(new ImageRecord
        {
            SourceId = sourceId, Path = Path.Combine(_root, name), FileSize = 10,
            ModifiedUtc = DateTime.UnixEpoch, IndexedUtc = DateTime.UtcNow, Status = ImageStatus.Active
        });

    [Fact]
    public void FailedImagesWithExistingFacesRemainRetryableInTheirModelSpace()
    {
        using var database = new PicerDatabase(Path.Combine(_root, "retry.db"));
        var source = database.AddSource(_root);
        var image = AddImage(database, source.Id, "retry.jpg");
        var repository = new VisionAnalysisRepository(database);
        var analyzer = new FixedAnalyzer("model-a");
        repository.SaveCompleted(image, analyzer, OneFace());
        repository.SaveFailure(image, analyzer, "face_image_missing");
        Assert.Contains(image.Id, repository.GetImageIdsWithFaces("model-a"));
        Assert.Contains(image.Id, repository.GetFailedImageIds(analyzer.AnalyzerName, "model-a"));
        Assert.Empty(repository.GetFailedImageIds(analyzer.AnalyzerName, "model-b"));
        repository.SaveServiceCompleted(image, analyzer, 1);
        Assert.Empty(repository.GetFailedImageIds(analyzer.AnalyzerName, "model-a"));
    }

    [Fact]
    public void ServiceCompletedMarkerSkipsReanalysisWithoutInventingLocalFaces()
    {
        using var database = new PicerDatabase(Path.Combine(_root, "index.db"));
        var source = database.AddSource(_root);
        var image = AddImage(database, source.Id, "one.jpg");
        var repository = new VisionAnalysisRepository(database);
        var analyzer = new FixedAnalyzer("insightface-arcface");

        Assert.False(repository.IsCurrent(image, analyzer.AnalyzerName, analyzer.ModelVersion));
        repository.SaveServiceCompleted(image, analyzer, 2);

        Assert.True(repository.IsCurrent(image, analyzer.AnalyzerName, analyzer.ModelVersion));
        // The two faces live in the photo space; the mirror must not fabricate local rows for them.
        Assert.Equal(0, repository.GetFaceCount(image.Id));
        Assert.DoesNotContain(image.Id, repository.GetImageIdsWithFaces(analyzer.ModelVersion));
    }

    [Fact]
    public void ImageIdsWithFacesOnlyCoverLiveFacesOfTheRequestedModelSpace()
    {
        using var database = new PicerDatabase(Path.Combine(_root, "index.db"));
        var source = database.AddSource(_root);
        var current = AddImage(database, source.Id, "current.jpg");
        var otherSpace = AddImage(database, source.Id, "other.jpg");
        var archived = AddImage(database, source.Id, "archived.jpg");
        var repository = new VisionAnalysisRepository(database);
        repository.SaveCompleted(current, new FixedAnalyzer("insightface-arcface"), OneFace());
        repository.SaveCompleted(otherSpace, new FixedAnalyzer("adaptive:other"), OneFace());
        repository.SaveCompleted(archived, new FixedAnalyzer("insightface-arcface"), OneFace());
        using (var archive = database.Command("UPDATE detected_faces SET is_archived=1 WHERE image_id=$id"))
        {
            archive.Parameters.AddWithValue("$id", archived.Id);
            archive.ExecuteNonQuery();
        }

        Assert.Equal([current.Id], repository.GetImageIdsWithFaces("insightface-arcface"));
        Assert.Equal([otherSpace.Id], repository.GetImageIdsWithFaces("adaptive:other"));
    }

    [Fact]
    public void ReadActiveTrainingArtifactReturnsArtifactAndPersonNamesFromAnotherLibrary()
    {
        var path = Path.Combine(_root, "local-index.db");
        long personId;
        using (var database = new PicerDatabase(path))
        {
            personId = new PeopleService(database).Create("test-person").Id;
            var repository = new VisionAnalysisRepository(database);
            repository.ApplyAgentTrainingArtifact(new TrainingArtifactData(
                "artifact-local", "test", "model-a", DateTime.UtcNow,
                [new(personId, [1f, 0f], 0.7, 4)], new(4, 1, 1, 1, 0, 0, 1, 0)));
            // The artifact and its calibration are stored apart; both must leave with the import.
            repository.SaveFaceMatchThreshold("model-a", 0.415, 12, 340);
        }
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();

        var read = VisionAnalysisRepository.ReadActiveTrainingArtifact(connection, "model-a");

        Assert.NotNull(read);
        Assert.Equal("artifact-local", read!.Value.Artifact.ArtifactId);
        Assert.Equal(personId, Assert.Single(read.Value.Artifact.Prototypes).PersonId);
        Assert.Equal("test-person", read.Value.PersonNames[personId]);
        var setting = Assert.IsType<FaceModelSetting>(read.Value.Setting);
        Assert.Equal(("model-a", 0.415, 12, 340), (setting.ModelVersion, setting.MatchThreshold, setting.CalibratedSamePairs, setting.CalibratedDifferentPairs));
        // Another model space has no active artifact in that library.
        Assert.Null(VisionAnalysisRepository.ReadActiveTrainingArtifact(connection, "model-b"));
    }

    [Fact]
    public void ReadActiveTrainingArtifactReportsNoCalibrationForAnUncalibratedSpace()
    {
        var path = Path.Combine(_root, "uncalibrated-index.db");
        using (var database = new PicerDatabase(path))
        {
            var personId = new PeopleService(database).Create("test-person").Id;
            new VisionAnalysisRepository(database).ApplyAgentTrainingArtifact(new TrainingArtifactData(
                "artifact-uncalibrated", "test", "model-a", DateTime.UtcNow,
                [new(personId, [1f, 0f], 0.7, 4)], new(4, 1, 1, 1, 0, 0, 1, 0)));
        }
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();

        var read = VisionAnalysisRepository.ReadActiveTrainingArtifact(connection, "model-a");

        Assert.NotNull(read);
        Assert.Equal("artifact-uncalibrated", read!.Value.Artifact.ArtifactId);
        Assert.Null(read.Value.Setting);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
