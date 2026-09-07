using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Search;
using Picer.Core.Vision;
using Picer.Core.Indexing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Picer.Tests;

public sealed class VisionAnalysisTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer_vision_" + Guid.NewGuid().ToString("n"));
    private readonly string _databasePath;
    private readonly PicerDatabase _database;
    private readonly ImageRecord _image;

    public VisionAnalysisTests()
    {
        Directory.CreateDirectory(_root);
        _databasePath = Path.Combine(_root, "vision.db");
        _database = new PicerDatabase(_databasePath);
        var source = _database.AddSource(_root);
        var path = Path.Combine(_root, "original.jpg");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
        var info = new FileInfo(path);
        _image = _database.UpsertImage(new ImageRecord
        {
            SourceId = source.Id,
            Path = path,
            FileSize = info.Length,
            ModifiedUtc = info.LastWriteTimeUtc,
            Status = ImageStatus.Active,
            IndexedUtc = DateTime.UtcNow
        });
    }

    [Fact]
    public void FaceFeatureOrderer_KeepsEmbeddingClustersTogetherAndMissingVectorsLast()
    {
        static UnassignedFaceRecord Face(long id) => new(id, id, $"{id}.jpg", null, 0,
            new NormalizedFaceBox(0, 0, 1, 1), 1);
        var faces = Enumerable.Range(1, 13).Select(index => Face(index)).ToArray();
        var embeddings = Enumerable.Range(1, 12).ToDictionary(index => (long)index,
            index => index <= 6 ? new[] { 1f, 0f } : new[] { 0f, 1f });

        var ordered = FaceFeatureOrderer.Order(faces, embeddings);

        Assert.Equal(13, ordered[^1].FaceId);
        Assert.True(ordered.Take(6).All(face => face.FaceId <= 6));
        Assert.True(ordered.Skip(6).Take(6).All(face => face.FaceId is >= 7 and <= 12));
    }

    [Fact]
    public void FaceLearningReport_IsReadOnlyAndFlagsEmptyPeopleSet()
    {
        var report = new FaceLearningReportService(new VisionAnalysisRepository(_database)).Generate();

        Assert.Equal("face-report-v1", report.ReportVersion);
        Assert.Equal(0, report.TotalPeople);
        Assert.Contains("尚未建立已命名人物", report.GlobalRisks);
        Assert.Contains("face-report-v1", report.ToJson());
    }

    [Fact]
    public void Database_MigratesVisionSchemaToVersion4()
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Assert.Equal((long)PicerDatabase.CurrentSchemaVersion, (long)version.ExecuteScalar()!);

        using var tables = connection.CreateCommand();
        tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('vision_analyses','detected_faces')";
        Assert.Equal(2L, (long)tables.ExecuteScalar()!);
    }

    [Fact]
    public async Task AnalyzeAsync_CachesUnchangedImageAndModel()
    {
        var analyzer = new FakeFaceAnalyzer("yunet-sface-1");
        var repository = new VisionAnalysisRepository(_database);
        using var service = new VisionAnalysisService(repository, analyzer);

        var first = await service.AnalyzeAsync([_image]);
        var second = await service.AnalyzeAsync([_image]);

        Assert.Equal(1, first.Analyzed);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(1, analyzer.Calls);
        Assert.Equal(1, repository.GetFaceCount(_image.Id));
    }

    [Fact]
    public async Task AnalyzeAsync_ReanalyzesAfterFileFingerprintOrModelChanges()
    {
        var repository = new VisionAnalysisRepository(_database);
        var firstAnalyzer = new FakeFaceAnalyzer("model-1");
        using (var service = new VisionAnalysisService(repository, firstAnalyzer))
        {
            await service.AnalyzeAsync([_image]);
            _image.FileSize++;
            await service.AnalyzeAsync([_image]);
        }

        var nextAnalyzer = new FakeFaceAnalyzer("model-2");
        using (var service = new VisionAnalysisService(repository, nextAnalyzer))
            await service.AnalyzeAsync([_image]);

        Assert.Equal(2, firstAnalyzer.Calls);
        Assert.Equal(1, nextAnalyzer.Calls);
        Assert.Equal("model-2", repository.Get(_image.Id, nextAnalyzer.AnalyzerName)!.ModelVersion);
    }

    [Fact]
    public async Task AnalyzeAsync_DoesNotModifySourceFile()
    {
        var before = File.ReadAllBytes(_image.Path);
        var beforeWrite = File.GetLastWriteTimeUtc(_image.Path);
        var repository = new VisionAnalysisRepository(_database);
        using var service = new VisionAnalysisService(repository, new FakeFaceAnalyzer("model-1"));

        await service.AnalyzeAsync([_image]);

        Assert.Equal(before, File.ReadAllBytes(_image.Path));
        Assert.Equal(beforeWrite, File.GetLastWriteTimeUtc(_image.Path));
    }

    [Fact]
    public async Task AnalyzeAsync_CancellationDoesNotWritePartialResult()
    {
        var analyzer = new CancelingFaceAnalyzer();
        var repository = new VisionAnalysisRepository(_database);
        using var service = new VisionAnalysisService(repository, analyzer);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.AnalyzeAsync([_image], cancellationToken: cancellation.Token));

        Assert.Null(repository.Get(_image.Id, analyzer.AnalyzerName));
    }

    [Fact]
    public async Task People_CanBeNamedFoundAndUsedToFindImages()
    {
        var repository = new VisionAnalysisRepository(_database);
        using var analysis = new VisionAnalysisService(repository, new FakeFaceAnalyzer("model-1"));
        await analysis.AnalyzeAsync([_image]);
        var faceId = Assert.Single(repository.GetFaceIds(_image.Id));
        var people = new PeopleService(_database);
        var wife = people.Create("张华", ["老婆"]);

        people.ConfirmFace(faceId, wife.Id);

        Assert.Equal(wife.Id, people.FindByName("老婆")!.Id);
        Assert.Equal([_image.Id], people.GetImageIds(wife.Id));
        Assert.Equal(["张华"], people.GetConfirmedPersonNamesForImage(_image.Id));
    }

    [Fact]
    public async Task People_CreateAndAssignFaces_IsAtomicAndConfirmsEveryFace()
    {
        var repository = new VisionAnalysisRepository(_database);
        using var analysis = new VisionAnalysisService(repository, new SameIdentityAnalyzer());
        await analysis.AnalyzeAsync([_image]);
        var faceIds = repository.GetFaceIds(_image.Id);
        var people = new PeopleService(_database);

        var person = people.CreateAndAssignFaces("新人物", faceIds);

        Assert.Single(repository.GetConfirmedFaces(person.Id));
        Assert.Equal(faceIds.Count, people.GetAll().Single(value => value.Id == person.Id).ConfirmedFaceCount);
    }

    [Fact]
    public void People_DuplicateNamesAndAliasesCannotBeCreatedOrRenamed()
    {
        var people = new PeopleService(_database);
        var existing = people.Create("Alice", ["A Li Ce"]);
        var other = people.Create("Bob");

        Assert.Throws<InvalidOperationException>(() => people.Create("  alice  "));
        Assert.Throws<InvalidOperationException>(() => people.CreateAndAssignFaces("ALICE", []));
        Assert.Throws<InvalidOperationException>(() => people.Create("a li ce"));
        Assert.Throws<InvalidOperationException>(() => people.Rename(other.Id, "Alice"));
        Assert.Equal(existing.Id, people.FindByName("ALICE")!.Id);
        Assert.Equal("Bob", people.Get(other.Id)!.DisplayName);
    }

    [Fact]
    public void LearnedFaceArtifacts_CanBeAppliedAndRolledBackWithoutChangingPeople()
    {
        var people = new PeopleService(_database);
        var first = people.Create("training-first");
        var second = people.Create("training-second");
        var repository = new VisionAnalysisRepository(_database);
        const string firstJson = "{\"id\":\"artifact-1\",\"baseModelVersion\":\"sface-v1\",\"people\":[{\"personId\":1,\"prototype\":[1,0],\"threshold\":0.61}],\"metrics\":{\"falseAcceptRate\":0,\"falseRejectRate\":0,\"positiveEvaluated\":20,\"negativeEvaluated\":10}}";
        const string secondJson = "{\"id\":\"artifact-2\",\"baseModelVersion\":\"sface-v1\",\"adapter\":{\"type\":\"diagonal-cosine-v1\",\"weights\":[1.5,0.5]},\"people\":[{\"personId\":2,\"prototype\":[0,1],\"threshold\":0.66}],\"metrics\":{\"falseAcceptRate\":0,\"falseRejectRate\":0,\"positiveEvaluated\":20,\"negativeEvaluated\":10}}";
        repository.ApplyLearnedFaceArtifact("artifact-1", "dataset-1", "http://localhost", "sface-v1",
            firstJson, 0, 0, [new(first.Id, "artifact-1", "sface-v1", [1, 0], .61)]);
        repository.ApplyLearnedFaceArtifact("artifact-2", "dataset-2", "http://localhost", "sface-v1",
            secondJson, 0, 0, [new(second.Id, "artifact-2", "sface-v1", [0, 1], .66)]);
        Assert.Equal([1.5f, .5f], repository.GetActiveFaceAdapter("sface-v1"));

        using var training = new FaceTrainingService(repository);
        Assert.Equal("artifact-1", training.Rollback());
        Assert.Null(repository.GetActiveFaceAdapter("sface-v1"));

        var policies = repository.GetLearnedFacePolicies("sface-v1");
        Assert.Single(policies);
        Assert.Equal(first.Id, policies.Single().Key);
        Assert.Equal(.61, policies.Single().Value.MatchThreshold, 3);
        Assert.Equal(2, people.GetAll().Count);
    }

    [Fact]
    public async Task FaceTrainingPlan_KeepsBurstFacesInOneDeterministicLeakageGroup()
    {
        var secondPath = Path.Combine(_root, "burst-second.jpg");
        File.WriteAllBytes(secondPath, [6, 7, 8]);
        var source = _database.GetSources().Single();
        var taken = new DateTime(2026, 9, 4, 8, 0, 0, DateTimeKind.Utc);
        using (var update = _database.Command("UPDATE images SET taken_utc=$taken,ahash=$hash,dhash=0,width=100,height=100 WHERE id=$id"))
        {
            update.Parameters.AddWithValue("$taken", taken.ToString("o")); update.Parameters.AddWithValue("$hash", 0x1110);
            update.Parameters.AddWithValue("$id", _image.Id); update.ExecuteNonQuery();
        }
        var second = _database.UpsertImage(new ImageRecord { SourceId = source.Id, Path = secondPath, FileSize = 3,
            ModifiedUtc = taken.AddSeconds(2), TakenUtc = taken.AddSeconds(2), Width = 100, Height = 100,
            AverageHash = 0x1111, DifferenceHash = 0, Status = ImageStatus.Active, IndexedUtc = DateTime.UtcNow });
        var repository = new VisionAnalysisRepository(_database);
        using var analysis = new VisionAnalysisService(repository, new FakeFaceAnalyzer("training-model"));
        await analysis.AnalyzeAsync([_image, second]);
        var people = new PeopleService(_database);
        people.MoveFace(repository.GetFaceIds(_image.Id).Single(), people.Create("训练甲").Id);
        people.MoveFace(repository.GetFaceIds(second.Id).Single(), people.Create("训练乙").Id);
        Assert.Equal(1, new BurstGroupService(_database).RebuildSource(source.Id));

        using var training = new FaceTrainingService(repository);
        var first = training.Prepare();
        var again = training.Prepare();

        Assert.Equal(1, first.LeakageGroups);
        Assert.Equal(2, first.PositiveSamples);
        Assert.Equal(2, first.NegativeSamples);
        Assert.Equal(first.SnapshotId, again.SnapshotId);
        Assert.True(first.TrainSamples == 4 || first.ValidationSamples == 4 || first.TestSamples == 4);
    }

    [Fact]
    public async Task FaceEvaluationSnapshot_IsImmutableAndLeakageSafe()
    {
        var repository = new VisionAnalysisRepository(_database);
        using var analysis = new VisionAnalysisService(repository, new FakeFaceAnalyzer("snapshot-sface"));
        await analysis.AnalyzeAsync([_image]);
        var person = new PeopleService(_database).Create("snapshot-person");
        new PeopleService(_database).MoveFace(repository.GetFaceIds(_image.Id).Single(), person.Id);

        var snapshots = new FaceEvaluationSnapshotService(repository);
        var first = snapshots.CreateSnapshot(randomSeed: 42, adapterVersion: "adapter-none");
        var second = snapshots.CreateSnapshot(randomSeed: 42, adapterVersion: "adapter-none");
        Assert.Equal(first.SnapshotId, second.SnapshotId);
        Assert.NotEmpty(snapshots.GetMembers(first.SnapshotId));
        Assert.Equal(PicerDatabase.CurrentSchemaVersion, GetSchemaVersion());
        Assert.Single(snapshots.GetMembers(first.SnapshotId).GroupBy(member => member.LeakageGroup));
        Assert.Throws<SqliteException>(() =>
        {
            using var update = _database.Command("UPDATE face_dataset_members SET quality_score=0 WHERE snapshot_id=$id");
            update.Parameters.AddWithValue("$id", first.SnapshotId);
            update.ExecuteNonQuery();
        });
    }

    [Fact]
    public void FaceEvaluationMetrics_CalculatePersonAndGateDegradation()
    {
        var observations = new[]
        {
            new FaceEvaluationObservation(1, true, true, "p1", .9),
            new FaceEvaluationObservation(1, true, false, "p2", .5),
            new FaceEvaluationObservation(1, false, true, "n1", .8),
            new FaceEvaluationObservation(1, false, false, "n2", .8)
        };
        var metric = FaceEvaluationService.CalculateMetric("person", "1", observations);
        Assert.Equal(.5, metric.Precision, 3);
        Assert.Equal(.5, metric.Recall, 3);
        Assert.Equal(.5, metric.Far, 3);
        Assert.Equal(.5, metric.Frr, 3);
        Assert.Equal(.5, metric.UnknownRejectionRate, 3);

        var baseline = new FaceEvaluationResult("b", "s", "old", "test", metric, [metric], [metric], false, []);
        var candidateMetric = metric with { Far = .75 };
        var candidate = new FaceEvaluationResult("c", "s", "new", "test", candidateMetric, [candidateMetric], [candidateMetric], false, []);
        var comparison = new FaceEvaluationService(new VisionAnalysisRepository(_database)).Compare(baseline, candidate);
        Assert.False(comparison.Passed);
        Assert.Contains(comparison.BlockingReasons, reason => reason.Contains("FAR", StringComparison.OrdinalIgnoreCase));
    }

    private long GetSchemaVersion()
    {
        using var command = _database.Command("PRAGMA user_version");
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public async Task People_UserCorrectionPreventsRejectedClusterSuggestion()
    {
        var repository = new VisionAnalysisRepository(_database);
        using var analysis = new VisionAnalysisService(repository, new FakeFaceAnalyzer("model-1"));
        await analysis.AnalyzeAsync([_image]);
        var faceId = Assert.Single(repository.GetFaceIds(_image.Id));
        var people = new PeopleService(_database);
        var wrong = people.Create("错误人物");
        var correct = people.Create("正确人物");

        Assert.True(people.SuggestFace(faceId, wrong.Id, 0.91));
        people.MoveFace(faceId, correct.Id);

        Assert.False(people.SuggestFace(faceId, wrong.Id, 0.99));
        Assert.Equal([_image.Id], people.GetImageIds(correct.Id));
        Assert.Empty(people.GetImageIds(wrong.Id, confirmedOnly: false));
    }

    [Fact]
    public async Task People_IgnoredFaceIsExcludedUntilMovedBackToUnassigned()
    {
        var repository = new VisionAnalysisRepository(_database);
        using var analysis = new VisionAnalysisService(repository, new FakeFaceAnalyzer("model-1"));
        await analysis.AnalyzeAsync([_image]);
        var faceId = Assert.Single(repository.GetFaceIds(_image.Id));
        var people = new PeopleService(_database);

        Assert.True(people.IgnoreFace(faceId));
        Assert.False(people.IgnoreFace(faceId));

        Assert.Empty(repository.GetUnassignedFaces());
        Assert.Equal(faceId, Assert.Single(repository.GetIgnoredFaces()).FaceId);
        Assert.Equal(1, repository.GetIgnoredFaceCount());
        Assert.DoesNotContain(repository.GetEmbeddingsForMatching(), face => face.FaceId == faceId);

        people.MoveFaceToUnassigned(faceId);

        Assert.Empty(repository.GetIgnoredFaces());
        Assert.Equal(faceId, Assert.Single(repository.GetUnassignedFaces()).FaceId);
    }

    [Fact]
    public async Task People_MovingIgnoredFaceToPersonRestoresItToAutomaticWorkflow()
    {
        var repository = new VisionAnalysisRepository(_database);
        using var analysis = new VisionAnalysisService(repository, new FakeFaceAnalyzer("model-1"));
        await analysis.AnalyzeAsync([_image]);
        var faceId = Assert.Single(repository.GetFaceIds(_image.Id));
        var people = new PeopleService(_database);
        var person = people.Create("家人");
        people.IgnoreFace(faceId);

        people.MoveFace(faceId, person.Id);

        Assert.Empty(repository.GetIgnoredFaces());
        Assert.Equal(faceId, Assert.Single(repository.GetConfirmedFaces(person.Id)).FaceId);
    }

    [Fact]
    public async Task People_BatchMoveIsIdempotentAndReportsStaleUiAssignments()
    {
        var repository = new VisionAnalysisRepository(_database);
        using var analysis = new VisionAnalysisService(repository, new FakeFaceAnalyzer("model-1"));
        await analysis.AnalyzeAsync([_image]);
        var faceId = Assert.Single(repository.GetFaceIds(_image.Id));
        var people = new PeopleService(_database);
        var person = people.Create("目标人物");

        var first = people.MoveFaces([faceId], person.Id);
        var repeated = people.MoveFaces([faceId], person.Id);

        Assert.Equal(new FaceMoveResult(1, 1, 0), first);
        Assert.Equal(new FaceMoveResult(1, 0, 1), repeated);
        Assert.Empty(repository.GetUnassignedFaces());
        Assert.Equal(faceId, Assert.Single(repository.GetConfirmedFaces(person.Id)).FaceId);
    }

    [Fact]
    public async Task PeopleViews_ShowOnlyOneRepresentativeForIdenticalPhotoCopies()
    {
        using (var hash = _database.Command("UPDATE images SET sha256='identical-content' WHERE id=$id"))
        {
            hash.Parameters.AddWithValue("$id", _image.Id);
            hash.ExecuteNonQuery();
        }
        var copyPath = Path.Combine(_root, "copy.jpg");
        File.WriteAllBytes(copyPath, [1, 2, 3, 4, 5]);
        var copy = _database.UpsertImage(new ImageRecord
        {
            SourceId = _image.SourceId, Path = copyPath, FileSize = 5, ModifiedUtc = File.GetLastWriteTimeUtc(copyPath),
            Sha256 = "reencoded-content", Width = 640, Height = 480, AverageHash = 12345,
            Status = ImageStatus.Active, IndexedUtc = DateTime.UtcNow
        });
        using (var visualHash = _database.Command("UPDATE images SET width=640,height=480,ahash=12345 WHERE id=$id"))
        {
            visualHash.Parameters.AddWithValue("$id", _image.Id);
            visualHash.ExecuteNonQuery();
        }
        var repository = new VisionAnalysisRepository(_database);
        using var analysis = new VisionAnalysisService(repository, new FakeFaceAnalyzer("model-1"));
        await analysis.AnalyzeAsync([_image, copy]);

        Assert.Equal(2, repository.GetFaceIds(_image.Id).Count + repository.GetFaceIds(copy.Id).Count);
        Assert.Single(repository.GetUnassignedFaces());
        Assert.Equal(new FaceWorkQueueCounts(1, 0, 1), repository.GetFaceWorkQueueCounts());

        var people = new PeopleService(_database);
        var person = people.Create("duplicate-test-person");
        foreach (var faceId in repository.GetFaceIds(_image.Id).Concat(repository.GetFaceIds(copy.Id)))
            people.ConfirmFace(faceId, person.Id);
        Assert.Single(repository.GetPersonFaces(person.Id));
        Assert.Single(repository.GetConfirmedFaces(person.Id));
        Assert.Equal(new FaceWorkQueueCounts(0, 0, 0), repository.GetFaceWorkQueueCounts());

        foreach (var faceId in repository.GetFaceIds(_image.Id).Concat(repository.GetFaceIds(copy.Id)))
            people.IgnoreFace(faceId);
        Assert.Single(repository.GetIgnoredFaces());
    }

    [Fact]
    public async Task NamedPersonViews_CollapseSameFileNameOrShaEvenWhenFaceDetectionsDiffer()
    {
        using (var update = _database.Command("UPDATE images SET sha256='shared-content' WHERE id=$id"))
        {
            update.Parameters.AddWithValue("$id", _image.Id);
            update.ExecuteNonQuery();
        }
        var source = _database.GetSources().Single();
        var nested = Path.Combine(_root, "nested");
        Directory.CreateDirectory(nested);

        ImageRecord AddImage(string path, string sha)
        {
            File.WriteAllBytes(path, [8, 7, 6, 5]);
            return _database.UpsertImage(new ImageRecord
            {
                SourceId = source.Id, Path = path, FileSize = 4, Sha256 = sha,
                ModifiedUtc = File.GetLastWriteTimeUtc(path), Status = ImageStatus.Active,
                IndexedUtc = DateTime.UtcNow
            });
        }

        var sameName = AddImage(Path.Combine(nested, "original.jpg"), "different-content");
        var sameHash = AddImage(Path.Combine(_root, "hash-copy.jpg"), "second-shared-content");
        var hashTwin = AddImage(Path.Combine(_root, "hash-twin.jpg"), "second-shared-content");
        var repository = new VisionAnalysisRepository(_database);
        using var analysis = new VisionAnalysisService(repository, new FakeFaceAnalyzer("source-dedup-test"));
        await analysis.AnalyzeAsync([_image, sameName, sameHash, hashTwin]);
        using (var changeDetections = _database.Command("""
            UPDATE detected_faces SET ordinal=ordinal+image_id,box_x=0.65,box_y=0.65
            WHERE image_id IN ($sameName,$sameHash,$hashTwin)
            """))
        {
            changeDetections.Parameters.AddWithValue("$sameName", sameName.Id);
            changeDetections.Parameters.AddWithValue("$sameHash", sameHash.Id);
            changeDetections.Parameters.AddWithValue("$hashTwin", hashTwin.Id);
            changeDetections.ExecuteNonQuery();
        }
        var people = new PeopleService(_database);
        var sameNamePerson = people.Create("same-name-person");
        foreach (var faceId in repository.GetFaceIds(_image.Id).Concat(repository.GetFaceIds(sameName.Id)))
            people.ConfirmFace(faceId, sameNamePerson.Id);
        var sameHashPerson = people.Create("same-hash-person");
        foreach (var faceId in repository.GetFaceIds(sameHash.Id).Concat(repository.GetFaceIds(hashTwin.Id)))
            people.ConfirmFace(faceId, sameHashPerson.Id);

        Assert.Single(repository.GetPersonFaces(sameNamePerson.Id));
        Assert.Single(repository.GetConfirmedFaces(sameNamePerson.Id));
        Assert.Single(repository.GetPersonFaces(sameHashPerson.Id));
        Assert.Single(repository.GetConfirmedFaces(sameHashPerson.Id));
    }

    [Fact]
    public void SaveCompleted_DeduplicatesOverlappingDetectorResultsBeforeInsert()
    {
        var repository = new VisionAnalysisRepository(_database);
        var analyzer = new FakeFaceAnalyzer("duplicate-detector-model");
        var first = new FaceDetection(new NormalizedFaceBox(.1f, .1f, .3f, .3f), .98f, null, [1f, 0f]);
        var duplicate = new FaceDetection(new NormalizedFaceBox(.102f, .101f, .299f, .301f), .91f, null, [1f, 0f]);

        repository.SaveCompleted(_image, analyzer, new FaceAnalysisResult([first, duplicate]));

        Assert.Equal(1, repository.GetFaceCount(_image.Id));
        Assert.Equal(1, repository.Get(_image.Id, analyzer.AnalyzerName)!.FaceCount);
    }

    [Fact]
    public async Task People_MergeMovesConfirmedFacesAndPreservesAliases()
    {
        var repository = new VisionAnalysisRepository(_database);
        using var analysis = new VisionAnalysisService(repository, new FakeFaceAnalyzer("model-1"));
        await analysis.AnalyzeAsync([_image]);
        var faceId = Assert.Single(repository.GetFaceIds(_image.Id));
        var people = new PeopleService(_database);
        var source = people.Create("张华", ["老婆"]);
        var target = people.Create("家人");
        people.ConfirmFace(faceId, source.Id);

        people.Merge(source.Id, target.Id);

        Assert.Null(people.Get(source.Id));
        Assert.Equal([_image.Id], people.GetImageIds(target.Id));
        Assert.Equal(target.Id, people.FindByName("老婆")!.Id);
    }

    [Fact]
    public async Task People_AutomaticSuggestionsDoNotPutTwoFacesFromSamePhotoIntoOnePerson()
    {
        var repository = new VisionAnalysisRepository(_database);
        var analyzer = new TwoFaceAnalyzer();
        using var analysis = new VisionAnalysisService(repository, analyzer);
        await analysis.AnalyzeAsync([_image]);
        var faces = repository.GetFaceIds(_image.Id);
        Assert.Equal(2, faces.Count);
        var people = new PeopleService(_database);
        var person = people.Create("人物甲");

        Assert.True(people.SuggestFace(faces[0], person.Id, 0.95));
        Assert.False(people.SuggestFace(faces[1], person.Id, 0.99));
        people.ConfirmFace(faces[1], person.Id);

        Assert.Equal([_image.Id], people.GetImageIds(person.Id));
    }

    [Fact]
    public async Task YuNetSFaceAnalyzer_LoadsCachedModelsAndHandlesImageWithoutFaces()
    {
        var modelRoot = Environment.GetEnvironmentVariable("PICER_MODEL_ROOT") ?? @"D:\PicerData\model-probe";
        var detector = Path.Combine(modelRoot, "face_detection_yunet_2023mar.onnx");
        var recognizer = Path.Combine(modelRoot, "face_recognition_sface_2021dec.onnx");
        if (!File.Exists(detector) || !File.Exists(recognizer)) return;
        var path = Path.Combine(_root, "blank.png");
        using (var image = new Image<Rgb24>(320, 320, Color.White)) image.SaveAsPng(path);
        var info = new FileInfo(path);
        var record = new ImageRecord { Path = path, FileSize = info.Length, ModifiedUtc = info.LastWriteTimeUtc };
        using var analyzer = new YuNetSFaceAnalyzer(detector, recognizer);

        var result = await analyzer.AnalyzeAsync(record, CancellationToken.None);

        Assert.Empty(result.Faces);
        Assert.Contains("yunet-", analyzer.ModelVersion);
        Assert.Contains("+sface-", analyzer.ModelVersion);
    }

    [Fact]
    public async Task FaceThumbnail_IsCroppedCachedAndDoesNotModifySource()
    {
        var path = Path.Combine(_root, "crop-source.png");
        using (var image = new Image<Rgb24>(400, 300, Color.CornflowerBlue)) image.SaveAsPng(path);
        var before = File.ReadAllBytes(path);
        var service = new FaceThumbnailService(Path.Combine(_root, "face-cache"));
        var face = new UnassignedFaceRecord(99, _image.Id, path, null, 0,
            new NormalizedFaceBox(0.25f, 0.2f, 0.3f, 0.4f), 0.9f);

        var first = await service.GetOrCreateAsync(face);
        var second = await service.GetOrCreateAsync(face);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.True(File.Exists(first));
        Assert.Equal(before, File.ReadAllBytes(path));
        using var crop = Image.Load(first);
        Assert.Equal(160, crop.Width);
        Assert.Equal(160, crop.Height);
    }

    [Fact]
    public async Task YuNetSFaceAnalyzer_ProducesNormalizedEmbeddingsForRealGroupPhotoWhenAvailable()
    {
        var photo = @"G:\Apple照片\2000-04-08 - 2019-01-06 学生\Internal Storage\DCIM\100APPLE\IMG_0158.JPG";
        var modelRoot = Environment.GetEnvironmentVariable("PICER_MODEL_ROOT") ?? @"D:\PicerData\model-probe";
        var detector = Path.Combine(modelRoot, "face_detection_yunet_2023mar.onnx");
        var recognizer = Path.Combine(modelRoot, "face_recognition_sface_2021dec.onnx");
        if (!File.Exists(photo) || !File.Exists(detector) || !File.Exists(recognizer)) return;
        var info = new FileInfo(photo);
        var record = new ImageRecord { Path = photo, FileSize = info.Length, ModifiedUtc = info.LastWriteTimeUtc };
        using var analyzer = new YuNetSFaceAnalyzer(detector, recognizer);

        var result = await analyzer.AnalyzeAsync(record, CancellationToken.None);

        Assert.True(result.Faces.Count >= 8);
        Assert.All(result.Faces, face =>
        {
            Assert.Equal(128, face.Embedding!.Count);
            var norm = Math.Sqrt(face.Embedding.Sum(value => value * value));
            Assert.InRange(norm, 0.999, 1.001);
        });
    }

    [Fact]
    public async Task SuggestionsRemainPendingUntilConfirmedAndPersonFilterFindsOnlyConfirmedPhotos()
    {
        var repository = new VisionAnalysisRepository(_database);
        using var analysis = new VisionAnalysisService(repository, new FakeFaceAnalyzer("model-1"));
        await analysis.AnalyzeAsync([_image]);
        var firstFace = Assert.Single(repository.GetFaceIds(_image.Id));
        var secondPath = Path.Combine(_root, "second.jpg");
        File.WriteAllBytes(secondPath, [5, 4, 3, 2, 1]);
        var source = _database.GetSources().Single();
        var secondImage = _database.UpsertImage(new ImageRecord
        {
            SourceId = source.Id, Path = secondPath, FileSize = 5,
            ModifiedUtc = File.GetLastWriteTimeUtc(secondPath), Status = ImageStatus.Active,
            IndexedUtc = DateTime.UtcNow
        });
        await analysis.AnalyzeAsync([secondImage]);
        var secondFace = Assert.Single(repository.GetFaceIds(secondImage.Id));
        var people = new PeopleService(_database);
        var person = people.Create("家人", ["老婆"]);
        people.ConfirmFace(firstFace, person.Id);
        var suggestions = new FaceSuggestionEngine(repository, people).Suggest(0.9);
        var unchangedSuggestions = new FaceSuggestionEngine(repository, people).Suggest(0.9);

        Assert.Equal(1, suggestions.Suggested);
        Assert.Equal(0, unchangedSuggestions.Suggested);
        var suggestedGroup = Assert.Single(repository.GetSuggestedPersonGroups());
        Assert.Equal(person.Id, suggestedGroup.PersonId);
        Assert.Equal(secondFace, Assert.Single(suggestedGroup.Faces).FaceId);
        Assert.Empty(IndexSearchEngine.Search(_database, new SearchFilters { PersonId = person.Id })
            .Where(image => image.Id == secondImage.Id));

        people.ConfirmFace(secondFace, person.Id);
        var matches = IndexSearchEngine.Search(_database, new SearchFilters { PersonId = person.Id });
        Assert.Equal(2, matches.Count);
        Assert.Equal(person.Id, people.FindByName("老婆")!.Id);
    }

    [Fact]
    public async Task UnknownClustering_NeverPlacesTwoFacesFromSamePhotoInOneCandidateGroup()
    {
        var repository = new VisionAnalysisRepository(_database);
        using var analysis = new VisionAnalysisService(repository, new SameIdentityAnalyzer());
        await analysis.AnalyzeAsync([_image]);
        var secondPath = Path.Combine(_root, "cluster-second.jpg");
        File.WriteAllBytes(secondPath, [9, 8, 7]);
        var second = _database.UpsertImage(new ImageRecord
        {
            SourceId = _database.GetSources().Single().Id, Path = secondPath, FileSize = 3,
            ModifiedUtc = File.GetLastWriteTimeUtc(secondPath), Status = ImageStatus.Active,
            IndexedUtc = DateTime.UtcNow
        });
        await analysis.AnalyzeAsync([second]);

        var result = new UnknownFaceClusterer(repository).Rebuild(0.9);
        var cluster = Assert.Single(repository.GetCandidateClusters());

        Assert.Equal(1, result.ClusterCount);
        Assert.Equal(2, cluster.Faces.Count);
        Assert.Equal(2, cluster.Faces.Select(face => face.ImageId).Distinct().Count());
    }

    [Fact]
    public async Task UnknownClustering_DeduplicatesCopiedPhotosByContentAndVisualIdentity()
    {
        var repository = new VisionAnalysisRepository(_database);
        using var analysis = new VisionAnalysisService(repository, new SameIdentityAnalyzer());
        await analysis.AnalyzeAsync([_image]);
        var sourceId = _database.GetSources().Single().Id;

        async Task<ImageRecord> AddCopy(string name, string sha256)
        {
            var path = Path.Combine(_root, name);
            File.WriteAllBytes(path, [9, 8, 7]);
            var image = _database.UpsertImage(new ImageRecord
            {
                SourceId = sourceId, Path = path, FileSize = 3, Sha256 = sha256,
                Width = 640, Height = 480, AverageHash = 12345,
                ModifiedUtc = File.GetLastWriteTimeUtc(path), Status = ImageStatus.Active,
                IndexedUtc = DateTime.UtcNow
            });
            await analysis.AnalyzeAsync([image]);
            return image;
        }

        var original = await AddCopy("copied-base.jpg", "same-content");
        var exactCopy = await AddCopy("copied-exact.jpg", "same-content");
        var reencodedCopy = await AddCopy("copied-reencoded.jpg", "different-bytes");

        new UnknownFaceClusterer(repository).Rebuild(0.9);
        var cluster = Assert.Single(repository.GetCandidateClusters());

        Assert.Contains(cluster.Faces, face => face.ImageId == original.Id);
        Assert.DoesNotContain(cluster.Faces, face => face.ImageId == exactCopy.Id);
        Assert.DoesNotContain(cluster.Faces, face => face.ImageId == reencodedCopy.Id);
        Assert.Equal(2, cluster.Faces.Count);
    }

    [Fact]
    public async Task SuggestedGroups_HideNearDuplicatePhotosUsingImageAndFaceSignals()
    {
        var repository = new VisionAnalysisRepository(_database);
        using var analysis = new VisionAnalysisService(repository, new SameIdentityAnalyzer());
        var sourceId = _database.GetSources().Single().Id;

        async Task<long> Add(string name, long averageHash)
        {
            var path = Path.Combine(_root, name);
            File.WriteAllBytes(path, [9, 8, 7]);
            var image = _database.UpsertImage(new ImageRecord
            {
                SourceId = sourceId, Path = path, FileSize = 3, Sha256 = name,
                Width = 640, Height = 480, AverageHash = averageHash,
                ModifiedUtc = File.GetLastWriteTimeUtc(path), Status = ImageStatus.Active,
                IndexedUtc = DateTime.UtcNow
            });
            await analysis.AnalyzeAsync([image]);
            return Assert.Single(repository.GetFaceIds(image.Id));
        }

        var people = new PeopleService(_database);
        var person = people.Create("近重复人物");
        people.ConfirmFace(await Add("prototype.jpg", 0x5555), person.Id);
        await Add("near-a.jpg", 0x1110);
        await Add("near-b.jpg", 0x1111); // 一位感知哈希差异，且人脸向量与位置一致。
        await Add("distinct.jpg", 0x7777);

        new FaceSuggestionEngine(repository, people).Suggest(0.9);
        var group = Assert.Single(repository.GetSuggestedPersonGroups());

        Assert.Equal(2, group.Faces.Count);
        Assert.Single(group.Faces.Where(face => Path.GetFileName(face.ImagePath).StartsWith("near-", StringComparison.Ordinal)));
        Assert.Contains(group.Faces, face => Path.GetFileName(face.ImagePath) == "distinct.jpg");
    }

    [Fact]
    public async Task Suggestions_UseMultiplePersonPrototypesAndGeneralizedNegativeSamples()
    {
        var repository = new VisionAnalysisRepository(_database);
        using var analysis = new VisionAnalysisService(repository, new VectorByNameAnalyzer());
        var sourceId = _database.GetSources().Single().Id;
        async Task<long> Add(string name)
        {
            var path = Path.Combine(_root, name); File.WriteAllBytes(path, [1, 2, 3]);
            var image = _database.UpsertImage(new ImageRecord
            {
                SourceId = sourceId, Path = path, FileSize = 3, ModifiedUtc = File.GetLastWriteTimeUtc(path),
                Status = ImageStatus.Active, IndexedUtc = DateTime.UtcNow
            });
            await analysis.AnalyzeAsync([image]);
            return Assert.Single(repository.GetFaceIds(image.Id));
        }

        var people = new PeopleService(_database);
        var person = people.Create("multi-prototype-person");
        people.ConfirmFace(await Add("prototype-x.jpg"), person.Id);
        people.ConfirmFace(await Add("prototype-y.jpg"), person.Id);
        var yCandidate = await Add("candidate-y.jpg");

        new FaceSuggestionEngine(repository, people).Suggest(0.9);
        Assert.Equal(person.Id, repository.GetSuggestedPersonGroups().Single().PersonId);
        Assert.Contains(repository.GetSuggestedPersonGroups().Single().Faces, face => face.FaceId == yCandidate);

        var rejected = await Add("negative-diagonal.jpg");
        people.MarkNotThisPerson(rejected, person.Id);
        var similarNegative = await Add("candidate-diagonal.jpg");
        var learnedResult = new FaceSuggestionEngine(repository, people).Suggest(0.6);

        Assert.DoesNotContain(repository.GetSuggestedPersonGroups().SelectMany(group => group.Faces),
            face => face.FaceId == similarNegative);
        Assert.Equal(1, learnedResult.MultiPrototypePeople);
        Assert.True(learnedResult.NegativeConflictDeferred > 0);
        Assert.True(learnedResult.NegativeSamples > 0);
    }

    [Fact]
    public async Task FaceValidation_PersistsYuNetGroundTruthAndCalculatesMissRate()
    {
        var repository = new VisionAnalysisRepository(_database);
        using var analysis = new VisionAnalysisService(repository, new FakeFaceAnalyzer("validation-model"));
        await analysis.AnalyzeAsync([_image]);
        var validation = new FaceValidationService(_database, repository);

        validation.SaveGroundTruth(_image.Id, 2, "侧脸，弱光", "人工复核");
        var result = validation.EvaluateYuNet();

        Assert.Equal(1, result.LabeledImages);
        Assert.Equal(2, result.ExpectedFaces);
        Assert.Equal(1, result.DetectedFaces);
        Assert.Equal(1, result.MissedFaces);
        Assert.Equal(0.5, result.MissRate);
        Assert.Equal(1, result.Categories["侧脸"].Images);
        Assert.Equal(1, result.Categories["弱光"].MissedFaces);
    }

    [Fact]
    public async Task FaceValidation_CalibratesSFaceFromUserConfirmedPeopleOnly()
    {
        ImageRecord Add(string name)
        {
            var path = Path.Combine(_root, name); File.WriteAllBytes(path, [1, 2, 3]);
            return _database.UpsertImage(new ImageRecord
            {
                SourceId = _database.GetSources().Single().Id, Path = path, FileSize = 3,
                ModifiedUtc = File.GetLastWriteTimeUtc(path), Status = ImageStatus.Active, IndexedUtc = DateTime.UtcNow
            });
        }
        var images = new[] { Add("a-1.jpg"), Add("a-2.jpg"), Add("b-1.jpg"), Add("b-2.jpg") };
        var repository = new VisionAnalysisRepository(_database);
        using var analysis = new VisionAnalysisService(repository, new CalibrationAnalyzer());
        await analysis.AnalyzeAsync(images);
        var people = new PeopleService(_database);
        var first = people.Create("成员甲"); var second = people.Create("成员乙");
        foreach (var image in images.Take(2)) people.ConfirmFace(Assert.Single(repository.GetFaceIds(image.Id)), first.Id);
        foreach (var image in images.Skip(2)) people.ConfirmFace(Assert.Single(repository.GetFaceIds(image.Id)), second.Id);

        var result = new FaceValidationService(_database, repository).CalibrateSFace(minimumPhotosPerPerson: 2);

        Assert.Equal(2, result.ConfirmedPeople);
        Assert.Equal(2, result.QualifiedPeople);
        Assert.Equal(2, result.SamePairs);
        Assert.Equal(4, result.DifferentPairs);
        Assert.Equal(0, result.FalseAcceptRate);
        Assert.Equal(0, result.FalseRejectRate);
        var validation = new FaceValidationService(_database, repository);
        validation.ApplySFaceThreshold(result);
        Assert.Equal(result.RecommendedThreshold, repository.GetFaceMatchThreshold(result.ModelVersion));
    }

    [Fact]
    public async Task PersonAndGeoFilters_UseConfirmedIdentityAndGpsIntersection()
    {
        var repository = new VisionAnalysisRepository(_database);
        using var analysis = new VisionAnalysisService(repository, new FakeFaceAnalyzer("composite-model"));
        _image.Gps = "33.87, 109.94";
        _database.UpdateImage(_image);
        await analysis.AnalyzeAsync([_image]);
        var outsidePath = Path.Combine(_root, "outside.jpg");
        File.WriteAllBytes(outsidePath, [4, 4, 4]);
        var outside = _database.UpsertImage(new ImageRecord
        {
            SourceId = _database.GetSources().Single().Id, Path = outsidePath, FileSize = 3,
            ModifiedUtc = File.GetLastWriteTimeUtc(outsidePath), Gps = "39.90, 116.40",
            Status = ImageStatus.Active, IndexedUtc = DateTime.UtcNow
        });
        await analysis.AnalyzeAsync([outside]);
        var people = new PeopleService(_database);
        var wife = people.Create("张华", ["老婆"]);
        people.ConfirmFace(Assert.Single(repository.GetFaceIds(_image.Id)), wife.Id);
        people.ConfirmFace(Assert.Single(repository.GetFaceIds(outside.Id)), wife.Id);

        var mention = people.FindMention("找一下老婆在商洛的照片");
        var matches = IndexSearchEngine.Search(_database, new SearchFilters
        {
            PersonId = mention!.Person.Id,
            GeoBounds = new GeoBounds("商洛", 32.95, 34.42, 108.55, 111.10),
            IncludeMissing = false
        });

        Assert.Equal("商洛", mention.RemainingQuery);
        Assert.Equal(_image.Id, Assert.Single(matches).Id);
    }

    [Fact]
    public async Task VisualConcepts_AreCachedSearchableAndUserRejectionSurvivesModelUpgrade()
    {
        var repository = new VisualConceptRepository(_database);
        var analyzer = new FakeSemanticAnalyzer("clip-test-1");
        var service = new SemanticAnalysisService(repository, analyzer);

        var first = await service.AnalyzeAsync([_image]);
        var second = await service.AnalyzeAsync([_image]);
        var filters = new SearchFilters
        {
            IncludeMissing = false,
            VisualConceptIds = ["season.summer", "apparel.dress", "scene.mountain"]
        };

        Assert.Equal(1, first.Analyzed);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(1, analyzer.Calls);
        Assert.Equal(_image.Id, Assert.Single(IndexSearchEngine.Search(_database, filters)).Id);

        repository.Reject(_image.Id, "apparel.dress");
        var upgraded = new SemanticAnalysisService(repository, new FakeSemanticAnalyzer("clip-test-2"));
        await upgraded.AnalyzeAsync([_image]);

        Assert.Empty(IndexSearchEngine.Search(_database, filters));
    }

    [Fact]
    public void VisualQueryParser_ExtractsCompositeConditionsAndLeavesPlaceName()
    {
        var people = new PeopleService(_database);
        people.Create("张华", ["老婆"]);

        var mention = people.FindMention("找一下老婆在商洛夏天穿裙子在山上的照片");
        var visual = VisualConceptCatalog.Parse(mention!.RemainingQuery);

        Assert.Equal("商洛", visual.RemainingQuery);
        Assert.Equal(["season.summer", "apparel.dress", "scene.mountain"], visual.ConceptIds);
    }

    public void Dispose()
    {
        _database.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class FakeFaceAnalyzer(string modelVersion) : IImageFaceAnalyzer
    {
        public string AnalyzerName => "face";
        public string ModelVersion => modelVersion;
        public int Calls { get; private set; }

        public Task<FaceAnalysisResult> AnalyzeAsync(ImageRecord image, CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new FaceAnalysisResult([
                new FaceDetection(
                    new NormalizedFaceBox(0.1f, 0.2f, 0.3f, 0.4f),
                    0.95f,
                    [0.2f, 0.3f],
                    [0.1f, 0.2f, 0.3f])
            ]));
        }
    }

    private sealed class CancelingFaceAnalyzer : IImageFaceAnalyzer
    {
        public string AnalyzerName => "face";
        public string ModelVersion => "cancel-test";
        public Task<FaceAnalysisResult> AnalyzeAsync(ImageRecord image, CancellationToken cancellationToken) =>
            Task.FromCanceled<FaceAnalysisResult>(cancellationToken);
    }

    private sealed class TwoFaceAnalyzer : IImageFaceAnalyzer
    {
        public string AnalyzerName => "face";
        public string ModelVersion => "two-face";
        public Task<FaceAnalysisResult> AnalyzeAsync(ImageRecord image, CancellationToken cancellationToken) =>
            Task.FromResult(new FaceAnalysisResult([
                new FaceDetection(new NormalizedFaceBox(0.1f, 0.2f, 0.2f, 0.3f), 0.95f),
                new FaceDetection(new NormalizedFaceBox(0.6f, 0.2f, 0.2f, 0.3f), 0.96f)
            ]));
    }

    private sealed class SameIdentityAnalyzer : IImageFaceAnalyzer
    {
        public string AnalyzerName => "face";
        public string ModelVersion => "cluster-test";
        public Task<FaceAnalysisResult> AnalyzeAsync(ImageRecord image, CancellationToken cancellationToken)
        {
            var count = image.Path.EndsWith("original.jpg", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            return Task.FromResult(new FaceAnalysisResult(Enumerable.Range(0, count).Select(index =>
                new FaceDetection(new NormalizedFaceBox(0.1f + index * 0.4f, 0.2f, 0.2f, 0.3f),
                    0.95f, null, [1f, 0f, 0f])).ToArray()));
        }
    }

    private sealed class VectorByNameAnalyzer : IImageFaceAnalyzer
    {
        public string AnalyzerName => "face";
        public string ModelVersion => "multi-prototype-test";
        public Task<FaceAnalysisResult> AnalyzeAsync(ImageRecord image, CancellationToken cancellationToken)
        {
            var name = Path.GetFileName(image.Path);
            IReadOnlyList<float> vector = name.Contains("diagonal", StringComparison.Ordinal) ? [0.7071f, 0.7071f]
                : name.Contains("-y", StringComparison.Ordinal) ? [0f, 1f] : [1f, 0f];
            return Task.FromResult(new FaceAnalysisResult([
                new FaceDetection(new NormalizedFaceBox(.1f, .1f, .3f, .3f), .99f, null, vector)
            ]));
        }
    }

    private sealed class CalibrationAnalyzer : IImageFaceAnalyzer
    {
        public string AnalyzerName => "face";
        public string ModelVersion => "calibration-model";
        public Task<FaceAnalysisResult> AnalyzeAsync(ImageRecord image, CancellationToken cancellationToken)
        {
            IReadOnlyList<float> embedding = Path.GetFileName(image.Path).StartsWith("a-", StringComparison.Ordinal) ? [1f, 0f] : [0f, 1f];
            return Task.FromResult(new FaceAnalysisResult([
                new FaceDetection(new NormalizedFaceBox(.1f, .1f, .3f, .3f), .99f, null, embedding)
            ]));
        }
    }

    private sealed class FakeSemanticAnalyzer(string version) : IImageSemanticAnalyzer
    {
        public string AnalyzerName => "semantic";
        public string ModelVersion => version;
        public int Calls { get; private set; }

        public Task<SemanticAnalysisResult> AnalyzeAsync(ImageRecord image, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new SemanticAnalysisResult([
                new VisualConceptScore("season.summer", 0.82f),
                new VisualConceptScore("apparel.dress", 0.91f),
                new VisualConceptScore("scene.mountain", 0.88f)
            ]));
        }
    }
}
