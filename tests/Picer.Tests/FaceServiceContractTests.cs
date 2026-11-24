using System.Net;
using System.Text.Json;
using Picer.Core.Models;
using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class FaceServiceContractTests
{
    private sealed class StubHandler(string answer) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        public string? RequestMethod { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.RequestUri!.AbsolutePath.EndsWith("face/detect", StringComparison.Ordinal)
                ? await request.Content!.ReadAsStringAsync(cancellationToken) : null;
            RequestMethod = new Uri(request.RequestUri!, "api/service/v1/face/detect").AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task AnalyzerReadsTheCamelCaseAnswerOfTheService()
    {
        // Exactly what the service returns: Results.Json writes property names in camelCase.
        var answer = JsonSerializer.Serialize(
            new FaceServiceResult(FaceServiceContract.CurrentSchema, "detect", "model-a",
                [new FaceServiceFace(0, null, .2f, .1f, .3f, .4f, .98f, [1f, 0f, 0f])]),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var handler = new StubHandler(answer);
        using var analyzer = new ServiceFaceAnalyzer(new Uri("https://service.test/"), new HttpClient(handler), "model-a");

        var result = await analyzer.AnalyzeAsync(new ImageRecord { Id = 7, Path = @"C:\Photos\one.jpg" }, CancellationToken.None);

        Assert.Equal("model-a", result.ModelVersion);
        var face = Assert.Single(result.Faces);
        Assert.Equal(new NormalizedFaceBox(.2f, .1f, .3f, .4f), face.Box);
        Assert.Equal(.98f, face.Confidence);
        Assert.Equal([1f, 0f, 0f], face.Embedding);
        // The indexed identity and path travel with the request so the service can file the result.
        using var sent = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(7, sent.RootElement.GetProperty("imageId").GetInt64());
        Assert.Equal(@"C:\Photos\one.jpg", sent.RootElement.GetProperty("path").GetString());
        Assert.Equal("model-a", sent.RootElement.GetProperty("modelVersion").GetString());
    }

    [Fact]
    public async Task AnalyzerRejectsAnAnswerFromAnotherModel()
    {
        using var handler = new StubHandler(JsonSerializer.Serialize(
            new FaceServiceResult(FaceServiceContract.CurrentSchema, "detect", "model-b", []), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var analyzer = new ServiceFaceAnalyzer(new Uri("https://service.test/"), new HttpClient(handler), "model-a");

        var reason = await Record.ExceptionAsync(() =>
            analyzer.AnalyzeAsync(new ImageRecord { Id = 7, Path = @"C:\Photos\one.jpg" }, CancellationToken.None));

        Assert.Contains("model-b", Assert.IsType<InvalidOperationException>(reason).Message);
    }

    [Fact]
    public void ValidImageResult_IsAccepted()
    {
        var result = new FaceServiceResult(FaceServiceContract.CurrentSchema, "embed", "insightface:v1",
            [new FaceServiceFace(0, null, .1f, .2f, .3f, .4f, .98f, [1, 2, 3])]);
        Assert.True(FaceServiceContract.IsValid(result, "embed"));
    }

    [Theory]
    [InlineData(-.01f, .2f, .3f, .4f)]
    [InlineData(.1f, .2f, 1.1f, .4f)]
    [InlineData(.1f, .2f, .3f, .4f)]
    public void InvalidBoxesOrConfidence_AreRejected(float x, float y, float width, float height)
    {
        var confidence = x < 0 ? .5f : 1.1f;
        var result = new FaceServiceResult(FaceServiceContract.CurrentSchema, "detect", "model", [new FaceServiceFace(0, null, x, y, width, height, confidence)]);
        Assert.False(FaceServiceContract.IsValid(result, "detect"));
    }

    [Fact]
    public void WrongSchemaOrOperation_IsRejected()
    {
        var result = new FaceServiceResult("old", "embed", "model", []);
        Assert.False(FaceServiceContract.IsValid(result, "detect"));
    }
}
