using Picer.ModelServer;

namespace Picer.Tests;

public sealed class TrainingMetricsEvaluatorTests
{
    private static readonly PersonPrototype[] Prototypes = [new(1, [1, 0], .8, 2), new(2, [0, 1], .8, 2)];
    private static readonly FaceSample[] Samples = [new(1, 1, [1, 0]), new(2, 2, [0, 1]),
        new(3, 1, [1, 0], "validation"), new(4, 2, [0, 1], "validation")];

    [Fact]
    public void NoValidationCannotFallBackToTraining() =>
        Assert.Throws<InvalidOperationException>(() => TrainingMetricsEvaluator.Evaluate(Prototypes, Samples[..2], []));

    [Fact]
    public void MissingNegativeEvidenceCannotPassGate()
    {
        var metrics = TrainingMetricsEvaluator.Evaluate(Prototypes, Samples, []);
        Assert.Equal(1, metrics.ValidationAccuracy);
        Assert.Equal(1, metrics.FalseAcceptRate);
        Assert.False(CalibrationGate.Passes(metrics));
    }

    [Fact]
    public void ExclusiveNegativeDoesNotRejectItsCorrectOtherIdentity()
    {
        var metrics = TrainingMetricsEvaluator.Evaluate(Prototypes, Samples, [new(4, 1, [0, 1])]);
        Assert.Equal(0, metrics.FalseAcceptRate);
        Assert.True(CalibrationGate.Passes(metrics));
    }

    [Fact]
    public void NegativeFalseAcceptIsNotDilutedByManyPositiveSamples()
    {
        var many = Samples.Concat(Enumerable.Range(10, 100).Select(id => new FaceSample(id, 1, [1, 0], "validation"))).ToArray();
        var metrics = TrainingMetricsEvaluator.Evaluate(Prototypes, many, [new(200, 1, [1, 0])]);
        Assert.Equal(1, metrics.FalseAcceptRate);
        Assert.False(CalibrationGate.Passes(metrics));
    }

    [Fact]
    public void RejectedPositiveCountsAsFalseRejection()
    {
        var metrics = TrainingMetricsEvaluator.Evaluate(Prototypes, [..Samples[..3], new(4, 2, [-1, 0], "validation")],
            [new(5, 1, [0, 1])]);
        Assert.Equal(.5, metrics.FalseRejectRate);
        Assert.Equal(.5, metrics.ValidationAccuracy);
        Assert.Equal(1, metrics.TrainAccuracy);
    }
}
