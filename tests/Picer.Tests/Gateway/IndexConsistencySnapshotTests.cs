using Picer.Core.Gateway;

namespace Picer.Tests;

public sealed class IndexConsistencySnapshotTests
{
    [Fact]
    public void MatchingSnapshotsRequireEpochMediaAndMetadataEvidence()
    {
        var left = new IndexConsistencySnapshot("epoch-1", 4, 7, 12, 3, "media", "metadata", 0, 0);
        var right = left with { };

        Assert.True(left.Matches(right));
        Assert.False(left.Matches(right with { MetadataDigest = "changed" }));
        Assert.False(left.Matches(right with { Epoch = "epoch-2" }));
        Assert.False(left.Matches(right with { PendingDescriptions = 1 }));
        Assert.True(left.Matches(right with { PendingDescriptions = 1 }, requireNoPendingWrites: false));
    }
}
