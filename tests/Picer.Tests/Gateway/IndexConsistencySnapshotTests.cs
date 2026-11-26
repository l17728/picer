using System.Reflection;
using Picer.Core.Data;
using Picer.Core.Gateway;
using Picer.Core.Thumbnails;

namespace Picer.Tests;

public sealed class IndexConsistencySnapshotTests
{
    [Fact]
    public void RemoteSnapshotRevisionMustRemainStableAcrossAllPages()
    {
        var before = new GatewayChangePage("epoch-1", 14, 0, false, false, 8, []);

        Assert.True(IndexConsistencySnapshotBuilder.HasSameRevision(before, before with { }));
        Assert.False(IndexConsistencySnapshotBuilder.HasSameRevision(before, before with { Through = 15 }));
        Assert.False(IndexConsistencySnapshotBuilder.HasSameRevision(before, before with { MetadataRevision = 9 }));
        Assert.False(IndexConsistencySnapshotBuilder.HasSameRevision(before, before with { Epoch = "epoch-2" }));
    }

    [Fact]
    public async Task CaptureRemoteRejectsRevisionChangeDuringCatalogRead()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-consistency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new PicerDatabase(Path.Combine(root, "index.db"));
            var target = new LocalGateway(database, new ThumbnailService(Path.Combine(root, "thumbs")));
            var gateway = DispatchProxy.Create<IIndexGateway, RevisionChangingGateway>();
            ((RevisionChangingGateway)(object)gateway).Initialize(target);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                IndexConsistencySnapshotBuilder.CaptureRemoteAsync(gateway));

            Assert.Equal("index_consistency_snapshot_changed", exception.Message);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

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

    public class RevisionChangingGateway : DispatchProxy
    {
        private IIndexGateway? _target;
        private int _revisionReads;

        public void Initialize(IIndexGateway target) => _target = target;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var result = targetMethod!.Invoke(_target, args);
            return targetMethod.Name == nameof(IIndexGateway.GetChangesAsync)
                ? ChangeSecondReadAsync((Task<GatewayChangePage>)result!)
                : result;
        }

        private async Task<GatewayChangePage> ChangeSecondReadAsync(Task<GatewayChangePage> request)
        {
            var page = await request;
            return Interlocked.Increment(ref _revisionReads) == 2 ? page with { Through = page.Through + 1 } : page;
        }
    }
}
