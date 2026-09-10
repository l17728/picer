using Picer.Core.Data;
using Picer.Core.People;

namespace Picer.Tests;

public sealed class FaceActionLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-audit-" + Guid.NewGuid().ToString("N"));
    private readonly PicerDatabase _db;

    public FaceActionLogTests()
    {
        Directory.CreateDirectory(_root);
        _db = new PicerDatabase(Path.Combine(_root, "picer.db"));
    }

    [Fact]
    public void Record_And_Query_RoundTrips()
    {
        FaceActionLogService.Record(_db, "confirm", 101, 7, 7, [102, 103], [7, 7], 0.92, "sface-v1");
        FaceActionLogService.Record(_db, "reject", 201, null, 8, [202, 203], [8, 8], 0.85, "sface-v1");

        var all = FaceActionLogService.GetActions(_db);
        Assert.Equal(2, all.Count);
        Assert.Equal("reject", all[0].Action);
        Assert.Equal(201, all[0].FaceId);
        Assert.Null(all[0].PersonId);
        Assert.Equal([202, 203], all[0].CoClusteredFaceIds);
        Assert.Equal(0.85, all[0].ClusterConfidence);

        var confirms = FaceActionLogService.GetActions(_db, "confirm");
        Assert.Single(confirms);
        Assert.Equal(101, confirms[0].FaceId);
    }

    [Fact]
    public void FalseMergePairs_IdentifiesCrossPersonGrouping()
    {
        FaceActionLogService.Record(_db, "reject", 301, null, 9, [302, 303], [9, 10], 0.88, "sface-v1");
        var pairs = FaceActionLogService.GetFalseMergePairs(_db);
        Assert.Equal(2, pairs.Count);
        Assert.Contains(pairs, p => p.FaceB == 302 && p.PersonB == 9);
        Assert.Contains(pairs, p => p.FaceB == 303 && p.PersonB == 10);
    }

    [Fact]
    public void ConfirmedPairs_OnlySamePerson()
    {
        FaceActionLogService.Record(_db, "confirm", 401, 5, 5, [402, 403], [5, 6], 0.95, "sface-v1");
        var pairs = FaceActionLogService.GetConfirmedPairs(_db);
        Assert.Single(pairs);
        Assert.Equal(402, pairs[0].FaceB);
        Assert.Equal(5, pairs[0].PersonB);
    }

    [Fact]
    public void MoveOut_CreatesFalseMergePair()
    {
        FaceActionLogService.Record(_db, "move_out", 501, 15, 11, [502], [11], 0.90, "sface-v1");
        var pairs = FaceActionLogService.GetFalseMergePairs(_db);
        Assert.Single(pairs);
        Assert.Equal(502, pairs[0].FaceB);
        Assert.Equal(15, pairs[0].PersonA);
        Assert.Equal(11, pairs[0].PersonB);
    }

    [Fact]
    public void MarkErrorType_Persists()
    {
        FaceActionLogService.Record(_db, "reject", 601, null, 12, [602], [12], 0.7, "sface-v1");
        var entry = FaceActionLogService.GetActions(_db).Single();
        FaceActionLogService.MarkErrorType(_db, entry.Id, "false_merge");
        var updated = FaceActionLogService.GetActions(_db).Single();
        Assert.Equal("false_merge", updated.ErrorType);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }
}
