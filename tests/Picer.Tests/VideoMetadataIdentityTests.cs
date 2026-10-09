using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Search;
using Picer.Core.Video;

namespace Picer.Tests;

public sealed class VideoMetadataIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "video-edit-identity-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _db;
    private readonly VideoRepository _videos;
    private readonly long _source;
    public VideoMetadataIdentityTests()
    {
        _db = new(Path.Combine(_root, "test.db")); _videos = new(_db); _source = _db.AddSource(_root).Id;
    }

    private VideoRecord Add(long id, string? hash = null)
    {
        var video = new VideoRecord(id, _source, Path.Combine(_root, id + ".mp4"), 3, DateTime.UnixEpoch,
            null, 0, 0, null, null, "active") { Sha256 = hash ?? new string('a', 64) };
        _videos.UpsertMirrored(video, _source); return video;
    }

    [Fact]
    public void CrossReferenceReplayUsesLatestContentDecisionInsteadOfReferenceOrder()
    {
        Add(100); Add(200);
        var tag = SemanticEngine.AddTag(_db, "review").Id;
        var metadata = new VideoSearchMetadata(_db);
        metadata.SetTag(200, tag, false); metadata.SetTag(100, tag, true);
        metadata.SetConcept(200, "animal", "manual", "accepted");
        metadata.SetConcept(100, "animal", "manual", "rejected");
        _db.ReplayMetadataEdits();
        Assert.Contains(tag, metadata.GetTags(200)); Assert.DoesNotContain("animal", metadata.GetConcepts(200));
        Assert.Equal(2, _db.GetMetadataEdits().Count);
        Assert.All(_db.GetMetadataEdits(), edit => Assert.Equal(100, edit.EntityId));
    }

    [Fact]
    public void ReplacementAndLateRemoteEditCannotApplyOldContentMetadataToNewBytes()
    {
        var original = Add(100); Add(200);
        var tag = SemanticEngine.AddTag(_db, "old-content").Id;
        var metadata = new VideoSearchMetadata(_db);
        metadata.SetTag(100, tag, true); metadata.SetConcept(100, "animal", "manual");
        var late = _db.GetMetadataEdits().ToArray();
        _videos.UpsertMirrored(original with { Sha256 = new string('b', 64) }, _source);
        _db.MergeMetadataEdits(late); _db.ReplayMetadataEdits();
        Assert.Empty(metadata.GetTags(100)); Assert.Empty(metadata.GetConcepts(100));
        Assert.Contains(tag, metadata.GetTags(200)); Assert.Contains("animal", metadata.GetConcepts(200));
    }

    [Fact]
    public void MissingReferenceLegacyOperationNeverRebindsAfterIdReuse()
    {
        var tag = SemanticEngine.AddTag(_db, "orphan").Id;
        var edit = new MetadataEdit("video-tag-link", 999, tag.ToString(), "1", DateTime.UtcNow.ToString("O"), Guid.NewGuid().ToString("N"));
        Assert.Empty(_db.MergeMetadataEdits([edit]));
        Add(999); Assert.Empty(_db.MergeMetadataEdits([edit])); _db.ReplayMetadataEdits();
        Assert.Empty(new VideoSearchMetadata(_db).GetTags(999));
        using var state = _db.Command("SELECT video_content_identity FROM metadata_edits WHERE entity_id=999");
        Assert.StartsWith("orphan:", (string)state.ExecuteScalar()!);
    }

    [Fact]
    public void UnverifiedIdentityDoesNotCrossDatabasesAndPromotesWhenHashBecomesKnown()
    {
        var legacy = Add(100, "");
        var tag = SemanticEngine.AddTag(_db, "local-review").Id;
        new VideoSearchMetadata(_db).SetTag(100, tag, true);
        var edit = Assert.Single(_db.GetMetadataEdits()); Assert.StartsWith("local:", edit.VideoContentIdentity!);
        using var other = new PicerDatabase(Path.Combine(_root, "other.db"));
        var source = other.AddSource(_root).Id;
        new VideoRepository(other).UpsertMirrored(legacy, source);
        SemanticEngine.AddTag(other, "local-review");
        Assert.Empty(other.MergeMetadataEdits([edit])); Assert.Empty(new VideoSearchMetadata(other).GetTags(100));
        using (var rejected = other.Command("SELECT COUNT(*) FROM video_metadata_operation_bindings"))
            Assert.Equal(0L, rejected.ExecuteScalar());
        _videos.UpsertMirrored(legacy with { Sha256 = new string('a', 64) }, _source);
        _db.ReplayMetadataEdits();
        var verified = Assert.Single(_db.GetMetadataEdits());
        Assert.StartsWith("sha256:", verified.VideoContentIdentity!);
        Assert.Equal(edit.OperationId, verified.OperationId);
        Assert.Contains(tag, new VideoSearchMetadata(_db).GetTags(100));
        new VideoRepository(other).UpsertMirrored(legacy with { Sha256 = new string('a', 64) }, source);
        Assert.Equal(verified.OperationId, Assert.Single(other.MergeMetadataEdits([verified])).OperationId);
        Assert.Contains(tag, new VideoSearchMetadata(other).GetTags(100));
    }

    [Fact]
    public void CrossReferenceWinnerAcknowledgmentDoesNotKeepExportingDefeatedOperation()
    {
        Add(100); Add(200);
        var tag = SemanticEngine.AddTag(_db, "shared").Id;
        var older = new MetadataEdit("video-tag-link", 200, tag.ToString(), "0", DateTime.UnixEpoch.ToString("O"), new string('1', 32))
        { VideoContentIdentity = "sha256:" + new string('a', 64) };
        var newer = older with { EntityId = 100, Value = "1", UpdatedUtc = DateTime.UnixEpoch.AddMinutes(1).ToString("O"), OperationId = new string('2', 32) };
        _db.MergeMetadataEdits([newer]);
        var acknowledged = Assert.Single(_db.MergeMetadataEdits([older]));
        Assert.Equal(newer.OperationId, acknowledged.OperationId); Assert.Equal(100, acknowledged.EntityId);
        Assert.Equal(newer.OperationId, Assert.Single(_db.GetMetadataEdits()).OperationId);
        Assert.Contains(tag, new VideoSearchMetadata(_db).GetTags(200));
    }

    [Fact]
    public void ReferencePurgeReassignsLatestContentDecisionToSurvivor()
    {
        Add(100); Add(200);
        var tag = SemanticEngine.AddTag(_db, "retained").Id;
        var metadata = new VideoSearchMetadata(_db);
        metadata.SetTag(200, tag, false); metadata.SetTag(100, tag, true);
        new VideoRecordPurger(_db).Purge(100);
        _db.ReplayMetadataEdits();
        Assert.Contains(tag, metadata.GetTags(200));
        Assert.Equal(200, Assert.Single(_db.GetMetadataEdits()).EntityId);
    }

    [Fact]
    public void NewerLateOldContentEventCannotOverwriteNewContentJournal()
    {
        var original = Add(100); Add(200);
        var tag = SemanticEngine.AddTag(_db, "independent-choices").Id;
        var metadata = new VideoSearchMetadata(_db);
        metadata.SetTag(100, tag, true);
        var old = Assert.Single(_db.GetMetadataEdits());
        _videos.UpsertMirrored(original with { Sha256 = new string('b', 64) }, _source);
        metadata.SetTag(100, tag, false);
        _db.MergeMetadataEdits([old with { UpdatedUtc = DateTime.UtcNow.AddDays(1).ToString("O"), OperationId = Guid.NewGuid().ToString("N") }]);
        _db.ReplayMetadataEdits();
        Assert.Empty(metadata.GetTags(100)); Assert.Contains(tag, metadata.GetTags(200));
        Assert.Equal(2, _db.GetMetadataEdits().Count);
        Assert.Contains(_db.GetMetadataEdits(), edit => edit.EntityId == 100 && edit.Value == "0");
    }

    [Fact]
    public void NewManualChoiceAdvancesPastFutureTimestampOnAnotherCopy()
    {
        Add(100); Add(200);
        var tag = SemanticEngine.AddTag(_db, "clock-order").Id;
        var future = DateTime.UtcNow.AddDays(1).ToString("O");
        _db.MergeMetadataEdits([new MetadataEdit("video-tag-link", 100, tag.ToString(), "0", future, Guid.NewGuid().ToString("N"))
            { VideoContentIdentity = "sha256:" + new string('a', 64) }]);
        new VideoSearchMetadata(_db).SetTag(200, tag, true);
        _db.ReplayMetadataEdits();
        Assert.Contains(tag, new VideoSearchMetadata(_db).GetTags(100));
        Assert.True(StringComparer.Ordinal.Compare(Assert.Single(_db.GetMetadataEdits()).UpdatedUtc, future) > 0);
    }

    [Fact]
    public void OrphanOperationBindingSurvivesLaterFieldEdits()
    {
        var tag = SemanticEngine.AddTag(_db, "orphan-history").Id;
        var orphan = new MetadataEdit("video-tag-link", 999, tag.ToString(), "1", DateTime.UtcNow.ToString("O"), Guid.NewGuid().ToString("N"));
        _db.MergeMetadataEdits([orphan]); Add(999);
        new VideoSearchMetadata(_db).SetTag(999, tag, false);
        Assert.Empty(_db.MergeMetadataEdits([orphan]));
        Assert.Empty(new VideoSearchMetadata(_db).GetTags(999));
    }

    [Fact]
    public void ExistingVerifiedOperationCannotRebindToAnotherSha()
    {
        Add(100); Add(200, new string('b', 64));
        var tag = SemanticEngine.AddTag(_db, "immutable-operation").Id;
        new VideoSearchMetadata(_db).SetTag(100, tag, true);
        var edit = Assert.Single(_db.GetMetadataEdits());
        _db.MergeMetadataEdits([edit with { EntityId = 200, VideoContentIdentity = "sha256:" + new string('b', 64) }]);
        Assert.Contains(tag, new VideoSearchMetadata(_db).GetTags(100));
        Assert.Empty(new VideoSearchMetadata(_db).GetTags(200));
        Assert.Equal(edit.VideoContentIdentity, Assert.Single(_db.GetMetadataEdits()).VideoContentIdentity);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PurgeReconcilesLateOldContentBindingsAcrossOriginalReferences(bool lastReference)
    {
        var original = Add(100); Add(200);
        if (!lastReference) Add(300);
        var tag = SemanticEngine.AddTag(_db, "late-origin").Id;
        _videos.UpsertMirrored(original with { Sha256 = new string('b', 64) }, _source);
        new VideoSearchMetadata(_db).SetTag(100, tag, false);
        var operation = Guid.NewGuid().ToString("N");
        var oldIdentity = "sha256:" + new string('a', 64);
        _db.MergeMetadataEdits([new MetadataEdit("video-tag-link", 100, tag.ToString(), "1", DateTime.UtcNow.ToString("O"), operation)
            { VideoContentIdentity = oldIdentity }]);
        // Existing journals may retain the received reference ID even though
        // applying the bound event resolves another content representative.
        using (var legacy = _db.Command("UPDATE video_metadata_operation_bindings SET reference_id=100 WHERE operation_id=$op"))
        { legacy.Parameters.AddWithValue("$op", operation); legacy.ExecuteNonQuery(); }
        new VideoRecordPurger(_db).Purge(200);
        using var binding = _db.Command("SELECT reference_id FROM video_metadata_operation_bindings WHERE content_identity=$identity");
        binding.Parameters.AddWithValue("$identity", oldIdentity);
        if (lastReference) Assert.Null(binding.ExecuteScalar());
        else Assert.Equal(300L, binding.ExecuteScalar());
        using var newContent = _db.Command("SELECT COUNT(*) FROM video_metadata_operation_bindings WHERE content_identity=$identity");
        newContent.Parameters.AddWithValue("$identity", "sha256:" + new string('b', 64));
        Assert.Equal(1L, newContent.ExecuteScalar());
        _db.ReplayMetadataEdits();
        Assert.Empty(new VideoSearchMetadata(_db).GetTags(100));
        if (!lastReference) Assert.Contains(tag, new VideoSearchMetadata(_db).GetTags(300));
    }

    public void Dispose()
    { _db.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }
}
