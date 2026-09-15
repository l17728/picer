using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class FaceTrainingEndpointTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8090/picer", "http://127.0.0.1:8090/picer/api/face-training/")]
    [InlineData("http://127.0.0.1:8090/picer/api/face-training", "http://127.0.0.1:8090/picer/api/face-training/")]
    [InlineData("http://127.0.0.1:8091", "http://127.0.0.1:8091/api/face-training/")]
    public void NormalizeEndpoint_UsesUnifiedFaceTrainingRoute(string configured, string expected)
    {
        var actual = FaceTrainingService.NormalizeEndpoint(new Uri(configured));
        Assert.Equal(expected, actual.ToString());
    }
}
