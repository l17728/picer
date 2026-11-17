using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Search;
using Picer.Core.SemanticSearch;

namespace Picer.Tests;

public sealed class AiSemanticRecallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-recall-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _db;
    private readonly long _source;
    public AiSemanticRecallTests()
    {
        _db = new(Path.Combine(_root, "index.db")); _source = _db.AddSource(_root).Id;
    }
    private ImageRecord Add(string name) => _db.UpsertImage(new ImageRecord
    {
        SourceId = _source, Path = Path.Combine(_root, name), FileSize = 100, ModifiedUtc = DateTime.UtcNow,
        IndexedUtc = DateTime.UtcNow, Status = ImageStatus.Active
    });
    [Fact]
    public async Task EmptyCoverageDoesNotCreateModelClient()
    {
        var image = Add("empty.jpg");
        var service = new AiSemanticRecall(_db, () => throw new InvalidOperationException("must_not_call"));
        var result = await service.SearchAsync("forest", new HashSet<long> { image.Id }, default);
        Assert.Empty(result.ImageIds); Assert.Contains("尚无", result.Status);
    }
    [Fact]
    public async Task SearchUsesOnlyAllowedCurrentModelVectors()
    {
        var first = Add("first.jpg"); var excluded = Add("excluded.jpg"); var stale = Add("stale.jpg");
        var repo = new ClipSemanticRepository(_db);
        foreach (var image in new[] { first, excluded, stale }) repo.Upsert(image, ChineseClipEncoder.ModelVersion, new float[] { 1, 0 });
        using (var update = _db.Command("UPDATE images SET file_size=file_size+1 WHERE id=$id"))
        { update.Parameters.AddWithValue("$id", stale.Id); update.ExecuteNonQuery(); }
        var service = new AiSemanticRecall(_db, () => new Encoder());
        var result = await service.SearchAsync("forest", new HashSet<long> { first.Id, stale.Id }, default);
        Assert.Equal(first.Id, Assert.Single(result.ImageIds));
    }
    [Fact]
    public async Task TimeoutDegradesButCallerCancellationPropagates()
    {
        var image = Add("timeout.jpg");
        new ClipSemanticRepository(_db).Upsert(image, ChineseClipEncoder.ModelVersion, new float[] { 1, 0 });
        var encoder = new Encoder { Wait = true };
        var service = new AiSemanticRecall(_db, () => encoder, TimeSpan.FromMilliseconds(30));
        var result = await service.SearchAsync("forest", new HashSet<long> { image.Id }, default);
        Assert.Empty(result.ImageIds); Assert.Contains("超时", result.Status); Assert.True(encoder.Disposed);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SearchAsync("forest", new HashSet<long> { image.Id }, stop.Token));
    }
    private sealed class Encoder : IClipEmbeddingClient, IDisposable
    {
        public bool Wait { get; init; }
        public bool Disposed { get; private set; }
        public string ModelVersion => ChineseClipEncoder.ModelVersion;
        public async Task<float[]> EncodeTextAsync(string text, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); if (Wait) await Task.Delay(Timeout.Infinite, cancellationToken); return [1, 0]; }
        public Task<float[]> EncodeImageAsync(ImageRecord image, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;
    }
    public void Dispose() { _db.Dispose(); Directory.Delete(_root, true); }
}
