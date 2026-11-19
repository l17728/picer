using Picer.Core.Gateway;

namespace Picer.Tests;

public sealed class IndexModeSwitchTransactionTests
{
    [Fact]
    public void RequiresSyncAndConsistencyBeforeSwitchAndCleanup()
    {
        var transaction = new IndexModeSwitchTransaction(IndexBackendConfig.ServiceMode, IndexBackendConfig.LocalMode);

        transaction.Begin();
        transaction.BeginSync();
        transaction.MarkSyncAndConsistencyPassed();
        transaction.MarkSwitched();
        transaction.MarkCleanupCompleted();

        Assert.Equal(IndexModeSwitchState.Completed, transaction.State);
    }

    [Fact]
    public void FailurePreservesFailedStateAndCode()
    {
        var transaction = new IndexModeSwitchTransaction(IndexBackendConfig.LocalMode, IndexBackendConfig.ServiceMode);
        transaction.Begin();
        transaction.Fail("consistency_mismatch");

        Assert.Equal(IndexModeSwitchState.Failed, transaction.State);
        Assert.Equal("consistency_mismatch", transaction.FailureCode);
        Assert.Throws<InvalidOperationException>(() => transaction.BeginSync());
    }
}
