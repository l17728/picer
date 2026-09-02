using System.IO;
using System.IO.Compression;
using Picer.App;
using Picer.Core.Data;
using Picer.Core.Indexing;
using Picer.Core.Models;
using Picer.Core.People;
using Picer.Core.Search;
using Picer.Core.SemanticSearch;
using Picer.Core.Storage;
using Picer.Core.Thumbnails;
using Picer.Core.Vision;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Picer.E2E;

public sealed class LifecycleScenarioTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"picer-lifecycle-{Guid.NewGuid():N}");

    [Fact]
    public async Task PhotoLibrary_ImportClassifySearchLoseRelocateBackupAndRestart()
    {
        var photos = Path.Combine(_root, "photos");
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(photos);
        Directory.CreateDirectory(data);
        CreateImage(Path.Combine(photos, "family-beach.png"), new Rgb24(30, 110, 220));
        CreateImage(Path.Combine(photos, "city-night.jpg"), new Rgb24(20, 20, 40));

        var dbPath = Path.Combine(data, "picer.db");
        long beachId;
        long tagId;
        long albumId;
        string movedPath;
        using (var database = new PicerDatabase(dbPath))
        {
            var source = database.AddSource(photos);
            var thumbnails = new ThumbnailService(Path.Combine(data, "thumbs"));
            var engine = new IndexEngine(database, new LocalStorageProvider(), thumbnails);
            var first = await engine.IndexSourceAsync(source);
            Assert.Equal(2, first.Indexed);
            Assert.Equal(2, database.GetImages(source.Id).Count);

            var second = await engine.IndexSourceAsync(source);
            Assert.Equal(0, second.Indexed);
            Assert.Equal(2, second.Skipped);

            var beach = Assert.Single(IndexSearchEngine.Search(database,
                new SearchFilters { Query = "family-beach", IncludeMissing = false }));
            beachId = beach.Id;
            var tag = SemanticEngine.AddTag(database, "家庭旅行");
            tagId = tag.Id;
            SemanticEngine.AssignTag(database, beach.Id, tag.Id);
            var album = SemanticEngine.AddAlbum(database, "海边回忆");
            albumId = album.Id;
            SemanticEngine.AddToAlbum(database, album.Id, beach.Id);
            Assert.Single(IndexSearchEngine.Search(database,
                new SearchFilters { TagId = tag.Id, IncludeMissing = false }));

            var oldPath = beach.Path;
            movedPath = Path.Combine(_root, "relocated", "family-renamed.png");
            Directory.CreateDirectory(Path.GetDirectoryName(movedPath)!);
            File.Move(oldPath, movedPath);
            Assert.Contains((await engine.VerifyAsync()), image => image.Id == beach.Id);
            var relocation = await engine.RelocateAsync(beach.Id, movedPath);
            Assert.True(relocation.Success);
            Assert.Equal(ImageStatus.Active, database.GetImage(beach.Id)!.Status);

            var archive = Path.Combine(_root, "library.picer-backup");
            await new DataProtectionService(database, data).CreateBackupAsync(archive);
            var manifest = DataProtectionService.ValidateBackup(archive);
            Assert.Equal(PicerDatabase.CurrentSchemaVersion, manifest.DatabaseVersion);
            Assert.Contains("picer.db", manifest.Entries);

            using var zip = ZipFile.OpenRead(archive);
            zip.GetEntry("picer.db")!.ExtractToFile(Path.Combine(_root, "restored.db"), true);
        }

        using (var restarted = new PicerDatabase(dbPath))
        {
            Assert.Equal(movedPath, restarted.GetImage(beachId)!.Path);
            Assert.Equal("家庭旅行", Assert.Single(SemanticEngine.GetImageTags(restarted, beachId)).Name);
            Assert.Equal(beachId, Assert.Single(SemanticEngine.GetAlbumImages(restarted, albumId)).Id);
            Assert.Single(IndexSearchEngine.Search(restarted,
                new SearchFilters { TagId = tagId, IncludeMissing = false }));
        }
        using (var restored = new PicerDatabase(Path.Combine(_root, "restored.db")))
        {
            Assert.Equal(movedPath, restored.GetImage(beachId)!.Path);
            Assert.Equal("海边回忆", Assert.Single(SemanticEngine.GetAlbums(restored)).Name);
        }
    }

    [Fact]
    public async Task PeopleAndAi_UserCorrectionReviewSearchAndRestartRemainAuthoritative()
    {
        Directory.CreateDirectory(_root);
        var imagePath = Path.Combine(_root, "group.jpg");
        CreateImage(imagePath, new Rgb24(160, 110, 80));
        var dbPath = Path.Combine(_root, "people.db");
        long imageId;
        long correctPersonId;
        using (var database = new PicerDatabase(dbPath))
        {
            var source = database.AddSource(_root);
            var image = database.UpsertImage(new ImageRecord
            {
                SourceId = source.Id, Path = imagePath, FileSize = new FileInfo(imagePath).Length,
                ModifiedUtc = File.GetLastWriteTimeUtc(imagePath), IndexedUtc = DateTime.UtcNow,
                Status = ImageStatus.Active, Sha256 = "scenario"
            });
            imageId = image.Id;
            var vision = new VisionAnalysisRepository(database);
            using (var analysis = new VisionAnalysisService(vision, new ScenarioFaceAnalyzer()))
                Assert.Equal(1, (await analysis.AnalyzeAsync([image])).Analyzed);
            var faceId = Assert.Single(vision.GetFaceIds(image.Id));
            var people = new PeopleService(database);
            var wrong = people.Create("错误人物");
            var correct = people.Create("安倍昭", ["家人"]);
            correctPersonId = correct.Id;
            Assert.True(people.SuggestFace(faceId, wrong.Id, 0.95));
            people.MoveFace(faceId, correct.Id);
            Assert.False(people.SuggestFace(faceId, wrong.Id, 0.99));
            Assert.Equal(image.Id, Assert.Single(IndexSearchEngine.Search(database,
                new SearchFilters { PersonId = correct.Id, IncludeMissing = false })).Id);
            Assert.Equal(correct.Id, people.FindMention("查找安倍昭的照片")!.Person.Id);

            var florence = new FlorenceResultRepository(database);
            florence.SaveSuggested(image, FlorenceTask.Caption, FlorenceExecutionProvider.Cpu, "家庭成员在海边合影");
            Assert.Empty(IndexSearchEngine.Search(database, new SearchFilters { Query = "海边合影", IncludeMissing = false }));
            florence.Review(image.Id, FlorenceTask.Caption.ToString(), true);
            Assert.Single(IndexSearchEngine.Search(database, new SearchFilters { Query = "海边合影", IncludeMissing = false }));

            var clip = new ClipSemanticRepository(database);
            clip.Upsert(image, "scenario-clip", [1f, 0f, 0f]);
            var hit = Assert.Single(clip.Search([1f, 0f, 0f]));
            Assert.Equal(image.Id, hit.Image.Id);
            clip.SaveEvaluation("家庭海边", "scenario-clip", 1, [(hit, true)]);
        }

        using var restarted = new PicerDatabase(dbPath);
        var restartedPeople = new PeopleService(restarted);
        Assert.Equal(imageId, Assert.Single(restartedPeople.GetImageIds(correctPersonId)));
        Assert.Equal(correctPersonId, restartedPeople.FindByName("家人")!.Id);
        Assert.Single(IndexSearchEngine.Search(restarted,
            new SearchFilters { Query = "海边合影", PersonId = correctPersonId, IncludeMissing = false }));
    }

    private static void CreateImage(string path, Rgb24 color)
    {
        using var image = new Image<Rgb24>(48, 32, color);
        if (Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase)) image.SaveAsJpeg(path);
        else image.SaveAsPng(path);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class ScenarioFaceAnalyzer : IImageFaceAnalyzer, IDisposable
    {
        public string AnalyzerName => "scenario-face";
        public string ModelVersion => "scenario-v1";
        public Task<FaceAnalysisResult> AnalyzeAsync(ImageRecord image, CancellationToken cancellationToken) =>
            Task.FromResult(new FaceAnalysisResult([new FaceDetection(new(0.2f, 0.15f, 0.35f, 0.5f), 0.99f,
                Embedding: [1f, 0f, 0f, 0f])]));
        public void Dispose() { }
    }
}
