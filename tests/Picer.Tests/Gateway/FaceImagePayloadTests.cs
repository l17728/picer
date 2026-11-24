using System.Text.Json;
using System.Text.Json.Nodes;
using Picer.MediaAgent;

namespace Picer.Tests.Gateway;

public sealed class FaceImagePayloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-face-payload-" + Guid.NewGuid().ToString("N"));
    private readonly string _photos;
    private readonly byte[] _image = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6];

    public FaceImagePayloadTests()
    {
        _photos = Path.Combine(_root, "photos");
        Directory.CreateDirectory(_photos);
        File.WriteAllBytes(Path.Combine(_photos, "one.jpg"), _image);
    }

    public void Dispose() => Directory.Delete(_root, true);

    private static string Unchanged(string path) => path;

    private static async Task<JsonObject> Hydrate(string payload, Func<string, string> resolve, IReadOnlyList<string>? roots = null)
    {
        var hydrated = await FaceImagePayload.HydrateAsync(payload, roots ?? [], resolve, CancellationToken.None);
        return JsonNode.Parse(hydrated)?.AsObject() ?? throw new InvalidOperationException("hydrated payload is not a JSON object");
    }

    [Fact]
    public async Task ReplacesTheIndexedPathWithTheEncodedImageAndKeepsTheOtherFields()
    {
        var payload = JsonSerializer.Serialize(new { imageId = 7, path = Path.Combine(_photos, "one.jpg"), modelVersion = "model-a" });

        var hydrated = await Hydrate(payload, Unchanged, [_root]);

        Assert.Null(hydrated["path"]);
        Assert.Equal(_image, Convert.FromBase64String(hydrated["imageBase64"]!.GetValue<string>()));
        Assert.Equal(7, hydrated["imageId"]!.GetValue<long>());
        Assert.Equal("model-a", hydrated["modelVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task AcceptsForwardSlashesInTheIndexedPath()
    {
        var indexed = Path.Combine(_photos, "one.jpg").Replace(Path.DirectorySeparatorChar, '/');

        var hydrated = await Hydrate(JsonSerializer.Serialize(new { path = indexed }), Unchanged, [_root]);

        Assert.Equal(_image, Convert.FromBase64String(hydrated["imageBase64"]!.GetValue<string>()));
    }

    [Fact]
    public async Task AdministrativeSharePathIsResolvedToTheConfiguredRoot()
    {
        // Index records keep the share form of the source machine, which never starts with a local root.
        var share = "\\\\source-machine\\G$\\photos\\one.jpg";
        string Resolve(string path) => path == share ? Path.Combine(_photos, "one.jpg") : path;

        var hydrated = await Hydrate(JsonSerializer.Serialize(new { path = share }), Resolve, [_root]);

        Assert.Equal(_image, Convert.FromBase64String(hydrated["imageBase64"]!.GetValue<string>()));
    }

    [Fact]
    public async Task SharePathIsRejectedWhenNoRootMatchesIt()
    {
        var share = "\\\\other-machine\\G$\\photos\\one.jpg";

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            FaceImagePayload.HydrateAsync(JsonSerializer.Serialize(new { path = share }), [_root], Unchanged, CancellationToken.None));
    }

    [Fact]
    public async Task ResolverCannotWidenTheReadableRoots()
    {
        var outside = Path.Combine(Path.GetTempPath(), "picer-face-outside-" + Guid.NewGuid().ToString("N"), "private.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        try
        {
            await File.WriteAllTextAsync(outside, "not an image");
            string Resolve(string path) => outside;

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                FaceImagePayload.HydrateAsync(JsonSerializer.Serialize(new { path = Path.Combine(_photos, "one.jpg") }), [_root], Resolve, CancellationToken.None));
        }
        finally { Directory.Delete(Path.GetDirectoryName(outside)!, true); }
    }

    [Fact]
    public async Task PayloadThatAlreadyCarriesAnImageIsReturnedWithoutReadingAFile()
    {
        var payload = JsonSerializer.Serialize(new { imageBase64 = Convert.ToBase64String(_image), modelVersion = "model-a" });
        string Resolve(string path) => throw new InvalidOperationException("the resolver must not run");

        var hydrated = await FaceImagePayload.HydrateAsync(payload, [], Resolve, CancellationToken.None);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(payload), JsonNode.Parse(hydrated)));
    }

    [Fact]
    public async Task RequestWithoutPathOrImageIsRejected()
    {
        foreach (var payload in new[] { "{\"imageId\":1}", "{\"path\":\"   \"}", "[]" })
        {
            var reason = await Record.ExceptionAsync(() => FaceImagePayload.HydrateAsync(payload, [_root], Unchanged, CancellationToken.None));
            Assert.IsType<InvalidOperationException>(reason);
        }
    }

    [Theory]
    [InlineData("missing.jpg")]
    [InlineData("empty.jpg")]
    [InlineData("oversized.jpg")]
    public async Task MissingEmptyOrOversizedFilesAreRejectedBeforeReading(string name)
    {
        if (name == "empty.jpg") await File.WriteAllBytesAsync(Path.Combine(_photos, name), []);
        if (name == "oversized.jpg")
        {
            await using var sparse = new FileStream(Path.Combine(_photos, name), FileMode.CreateNew);
            sparse.SetLength(FaceImagePayload.MaxImageBytes + 1);
        }

        var reason = await Record.ExceptionAsync(() => FaceImagePayload.HydrateAsync(
            JsonSerializer.Serialize(new { path = Path.Combine(_photos, name) }), [_root], Unchanged, CancellationToken.None));

        Assert.IsType<InvalidDataException>(reason);
    }

    [Fact]
    public async Task FileAtTheLimitIsAccepted()
    {
        await using (var exact = new FileStream(Path.Combine(_photos, "limit.jpg"), FileMode.CreateNew))
            exact.SetLength(FaceImagePayload.MaxImageBytes);

        var hydrated = await Hydrate(JsonSerializer.Serialize(new { path = Path.Combine(_photos, "limit.jpg") }), Unchanged, [_root]);

        Assert.Equal(FaceImagePayload.MaxImageBytes, Convert.FromBase64String(hydrated["imageBase64"]!.GetValue<string>()).LongLength);
    }
}
