using Picer.ModelServer;

namespace Picer.Tests;

public sealed class TrainingRecipeBuilderTests
{
    [Fact]
    public void Build_DeduplicatesByHashAndPrefersUsableSample()
    {
        var dataset = new DatasetRequest("snap", Samples: [
            new FaceSample(1, 7, [1], Evidence: new SampleEvidence("user-confirmed", "same", GroupId: "g"), Attributes: new VlAttributes("front", "good", [], "neutral", .4, false, "v1", "p1")),
            new FaceSample(2, 7, [1], Evidence: new SampleEvidence("user-confirmed", "same", GroupId: "g"), Attributes: new VlAttributes("front", "good", [], "neutral", .9, true, "v1", "p1"))
        ]);

        var result = new TrainingRecipeBuilder().Build(dataset);

        Assert.Single(result);
        Assert.Equal(2, result[0].FaceId);
    }

    [Fact]
    public void Build_UsesGroupIdWhenHashMissing()
    {
        var dataset = new DatasetRequest("snap", Samples: [
            new FaceSample(1, 7, [1], Evidence: new SampleEvidence("user-confirmed", GroupId: "burst")),
            new FaceSample(2, 7, [1], Evidence: new SampleEvidence("user-confirmed", GroupId: "burst"))
        ]);

        Assert.Single(new TrainingRecipeBuilder().Build(dataset));
    }
}
