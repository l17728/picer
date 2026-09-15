using Picer.Core.People;

namespace Picer.Tests.People;

public sealed class FaceTrainingToolRegistryTests
{
    [Fact]
    public void RegistryContainsSeparatedReadPlanExecuteAndApplyTools()
    {
        Assert.Contains(FaceTrainingToolRegistry.Definitions, t => t.Name == "inspect_data_quality" && t.ReadOnly);
        Assert.Contains(FaceTrainingToolRegistry.Definitions, t => t.Name == "build_recipe" && t.ReadOnly);
        Assert.Contains(FaceTrainingToolRegistry.Definitions, t => t.Name == "submit_training_job" && !t.ReadOnly);
        var apply = Assert.Single(FaceTrainingToolRegistry.Definitions.Where(t => t.Name == "apply_artifact"));
        Assert.Contains("artifact:apply", apply.RequiredScopes);
    }

    [Fact]
    public void UnknownToolIsRejected()
    {
        Assert.False(FaceTrainingToolRegistry.TryGet("execute_sql", out _));
    }
}
