using Picer.Core.Vision;

namespace Picer.Tests;

public sealed class FaceTrainingEndpointTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8090/picer", "http://127.0.0.1:8090/picer/api/face-training/")]
    [InlineData("http://127.0.0.1:8090/picer/api/face-training", "http://127.0.0.1:8090/picer/api/face-training/")]
    [InlineData("http://127.0.0.1:8090/api/face-training", "http://127.0.0.1:8090/picer/api/face-training/")]
    [InlineData("http://127.0.0.1:8091", "http://127.0.0.1:8091/api/face-training/")]
    public void NormalizeEndpoint_UsesUnifiedFaceTrainingRoute(string configured, string expected)
    {
        var actual = FaceTrainingService.NormalizeEndpoint(new Uri(configured));
        Assert.Equal(expected, actual.ToString());
    }

    [Fact]
    public void ArtifactVersion_AcceptsBothGatewayFieldNames()
    {
        Assert.Equal("sface-v2", FaceTrainingService.ResolveArtifactModelVersion(null, "sface-v2"));
        Assert.Equal("sface-v2", FaceTrainingService.ResolveArtifactModelVersion("sface-v2", "old-value"));
        Assert.Null(FaceTrainingService.ResolveArtifactModelVersion(null, null));
    }

    [Fact]
    public void ArtifactHash_IgnoresIntegrityFields()
    {
        const string unsigned = "{\"artifactId\":\"a\",\"sha256\":null,\"signature\":null,\"signatureAlgorithm\":null,\"signingKeyId\":null}";
        const string signed = "{\"artifactId\":\"a\",\"sha256\":\"wrong\",\"signature\":\"sig\",\"signatureAlgorithm\":\"ecdsa-p256-sha256\",\"signingKeyId\":\"key\"}";
        Assert.Equal(FaceTrainingService.ComputeUnsignedArtifactHash(unsigned),
            FaceTrainingService.ComputeUnsignedArtifactHash(signed));
    }
}
