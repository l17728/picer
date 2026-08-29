using Picer.Core.FileOperations;

namespace Picer.Tests;

public sealed class FileOperationModelsTests
{
    [Fact]
    public void DeletionPlan_SnapshotsSelectionsAndCalculatesSummary()
    {
        var source = new List<AuthorizedFileSelection>
        {
            new(1, "a.jpg", 100, DateTime.UnixEpoch, "hash-a"),
            new(2, "b.jpg", 250, DateTime.UnixEpoch, "hash-b")
        };

        var plan = new DeletionPlan(Guid.NewGuid(), DateTime.UtcNow, source);
        source.Clear();

        Assert.Equal(2, plan.FileCount);
        Assert.Equal(350, plan.TotalBytes);
        Assert.Equal([1L, 2L], plan.Items.Select(item => item.ImageId));
    }

    [Fact]
    public void FileOperationResult_CountsOnlyVerifiedSuccesses()
    {
        var success = new AuthorizedFileSelection(1, "a.jpg", 100, DateTime.UnixEpoch, "hash-a");
        var failure = new AuthorizedFileSelection(2, "b.jpg", 250, DateTime.UnixEpoch, "hash-b");
        var result = new FileOperationResult(Guid.NewGuid(),
        [
            new(success, true, FileOperationFailure.None),
            new(failure, false, FileOperationFailure.FileChanged, "changed")
        ]);

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(100, result.SucceededBytes);
    }
}
