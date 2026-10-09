using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class FaceServiceContractTests
{
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
