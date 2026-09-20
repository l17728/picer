using Picer.Core.People;

namespace Picer.Tests;

public sealed class TrainingJobStateMachineTests
{
    [Theory]
    [InlineData("queued", "running")]
    [InlineData("running", "recoverable")]
    [InlineData("recoverable", "queued")]
    [InlineData("recoverable", "canceled")]
    [InlineData("running", "completed")]
    public void AllowsRecoveryAndTerminalTransitions(string from, string to) =>
        Assert.True(TrainingJobStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData("completed", "running")]
    [InlineData("canceled", "completed")]
    [InlineData("failed", "completed")]
    public void RejectsInvalidTransitions(string from, string to) =>
        Assert.False(TrainingJobStateMachine.CanTransition(from, to));

    [Fact]
    public void ValidateProducesAuditableTransition()
    {
        var transition = TrainingJobStateMachine.Validate("job-1", "running", "recoverable");
        Assert.Equal("job-1", transition.JobId);
        Assert.Equal("running", transition.From);
        Assert.Equal("recoverable", transition.To);
        Assert.True(transition.OccurredUtc.Kind is DateTimeKind.Utc or DateTimeKind.Local);
    }
}
