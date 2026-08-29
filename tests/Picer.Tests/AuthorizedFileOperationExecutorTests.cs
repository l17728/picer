using Picer.Core.FileOperations;
using Picer.Core.Hashing;

namespace Picer.Tests;

public sealed class AuthorizedFileOperationExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-recycle-" + Guid.NewGuid().ToString("N"));
    public AuthorizedFileOperationExecutorTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task Execute_RevalidatesSnapshotAndSupportsPartialSuccess()
    {
        var first = Create("first.jpg", [1, 2, 3]);
        var changed = Create("changed.jpg", [4, 5, 6]);
        File.AppendAllText(changed.Path, "changed");
        var gateway = new FakeRecycleBinGateway();
        var executor = new AuthorizedFileOperationExecutor(gateway);

        var result = await executor.MoveToRecycleBinAsync(new DeletionPlan(Guid.NewGuid(), DateTime.UtcNow, [first, changed]));

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.False(File.Exists(first.Path));
        Assert.True(File.Exists(changed.Path));
        Assert.Contains(result.Items, item => item.Failure == FileOperationFailure.FileChanged);
    }

    [Fact]
    public async Task Execute_GatewayFailureLeavesFileAndReportsFailure()
    {
        var selection = Create("denied.jpg", [7, 8, 9]);
        var executor = new AuthorizedFileOperationExecutor(new FakeRecycleBinGateway { Failure = new UnauthorizedAccessException("denied") });

        var result = await executor.MoveToRecycleBinAsync(new DeletionPlan(Guid.NewGuid(), DateTime.UtcNow, [selection]));

        Assert.True(File.Exists(selection.Path));
        Assert.Equal(FileOperationFailure.AccessDenied, Assert.Single(result.Items).Failure);
    }

    [Fact]
    public async Task Execute_PreCancelledPlanMakesNoGatewayCalls()
    {
        var selection = Create("cancel.jpg", [1]);
        var gateway = new FakeRecycleBinGateway();
        var executor = new AuthorizedFileOperationExecutor(gateway);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.MoveToRecycleBinAsync(
            new DeletionPlan(Guid.NewGuid(), DateTime.UtcNow, [selection]), cancellation.Token));
        Assert.Equal(0, gateway.Calls);
        Assert.True(File.Exists(selection.Path));
    }

    private AuthorizedFileSelection Create(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        var info = new FileInfo(path);
        return new(1, path, info.Length, info.LastWriteTimeUtc, FileHasher.ComputeSha256(path));
    }

    private sealed class FakeRecycleBinGateway : IRecycleBinGateway
    {
        public Exception? Failure { get; init; }
        public int Calls { get; private set; }
        public void MoveToRecycleBin(string path)
        {
            Calls++;
            if (Failure is not null) throw Failure;
            File.Delete(path);
        }
    }
}
