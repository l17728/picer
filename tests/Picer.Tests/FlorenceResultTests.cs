using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class FlorenceResultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"picer-florence-results-{Guid.NewGuid():N}"); private readonly PicerDatabase _db;
    public FlorenceResultTests() { Directory.CreateDirectory(_root); _db = new PicerDatabase(Path.Combine(_root, "picer.db")); }
    [Fact]
    public void AcceptedFlorenceDescription_EntersMainSearch()
    {
        var source = _db.AddSource(_root); var path = Path.Combine(_root, "neutral.jpg"); File.WriteAllBytes(path, [1, 2, 3]);
        var image = _db.UpsertImage(new ImageRecord { SourceId = source.Id, Path = path, FileSize = 3, ModifiedUtc = File.GetLastWriteTimeUtc(path), IndexedUtc = DateTime.UtcNow, Status = ImageStatus.Active });
        var repository = new FlorenceResultRepository(_db); repository.SaveSuggested(image, FlorenceTask.Caption, FlorenceExecutionProvider.Cpu, "a bright blue object");
        Assert.Empty(IndexSearchEngine.Search(_db, new SearchFilters { Query = "blue" }));
        repository.Review(image.Id, FlorenceTask.Caption.ToString(), true);
        Assert.Single(IndexSearchEngine.Search(_db, new SearchFilters { Query = "blue" }));
    }
    public void Dispose() { _db.Dispose(); try { Directory.Delete(_root, true); } catch { } }
}
