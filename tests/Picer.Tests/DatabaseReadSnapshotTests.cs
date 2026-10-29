using Microsoft.Data.Sqlite;
using Picer.Core.Data;

namespace Picer.Tests;

public sealed class DatabaseReadSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-read-snapshot-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SnapshotRemainsConsistentAcrossWriterCommitAndNextReadSeesCommit()
    {
        using var writer = CreateDatabase();
        var values = writer.ReadSnapshot(reader =>
        {
            // Commit after the snapshot was pinned, before its first application query.
            SetValue(writer, 2);
            var first = ReadValue(reader);
            SetValue(writer, 3);
            return (First: first, Second: ReadValue(reader));
        });
        Assert.Equal((1L, 1L), values);
        Assert.Equal(3L, writer.ReadSnapshot(ReadValue));
    }

    [Fact]
    public async Task ReadCompletesWhileWriterTransactionIsUncommitted()
    {
        using var writer = CreateDatabase();
        using var transaction = writer.BeginTransaction();
        SetValue(writer, 2);
        var reading = Task.Run(() => writer.ReadSnapshot(ReadValue));
        try { Assert.Equal(1L, await reading.WaitAsync(TimeSpan.FromSeconds(5))); }
        finally { transaction.Rollback(); await reading; }
    }

    [Fact]
    public void ReadConnectionRejectsWritesAndCallbackFailureReleasesSnapshot()
    {
        using var writer = CreateDatabase();
        using (var reader = writer.OpenReadOnlyConnection())
        {
            var error = Assert.Throws<SqliteException>(() => SetValue(reader, 2));
            Assert.Equal(8, error.SqliteErrorCode);
        }
        Assert.Throws<InvalidOperationException>(() => writer.ReadSnapshot<int>(_ => throw new InvalidOperationException()));
        SetValue(writer, 3);
        Assert.Equal(3L, writer.ReadSnapshot(ReadValue));
        using var checkpoint = writer.Command("PRAGMA wal_checkpoint(TRUNCATE)");
        using var result = checkpoint.ExecuteReader();
        Assert.True(result.Read());
        Assert.Equal(0, result.GetInt32(0));
    }

    [Fact]
    public void CancellationDoesNotInvokeCallbackAndMemoryDatabaseRequiresFallback()
    {
        using var writer = CreateDatabase();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => writer.ReadSnapshot<int>(_ => throw new Xunit.Sdk.XunitException("callback invoked"), cancellation.Token));
        using var memory = new PicerDatabase(":memory:");
        Assert.False(memory.SupportsIndependentReadSnapshots);
        Assert.Throws<NotSupportedException>(() => memory.OpenReadOnlyConnection());
    }

    [Fact]
    public void ReviewQueriesReusePinnedSnapshotWithoutDisposingIt()
    {
        using var writer = CreateDatabase();
        writer.ReadSnapshot(reader =>
        {
            SetValue(writer, 2);
            Assert.Empty(reader.GetVideoOwnershipReviews());
            Assert.Empty(reader.GetMetadataConflictSnapshot().Reviews);
            Assert.Empty(reader.GetCatalogRetirementReviews());
            Assert.Equal(1L, ReadValue(reader));
            return true;
        });
        Assert.Equal(2L, writer.ReadSnapshot(ReadValue));
    }

    private PicerDatabase CreateDatabase()
    {
        var database = new PicerDatabase(Path.Combine(_root, "picer.db"));
        using var command = database.Command("CREATE TABLE read_probe(value INTEGER NOT NULL); INSERT INTO read_probe VALUES(1)");
        command.ExecuteNonQuery();
        return database;
    }

    private static long ReadValue(PicerDatabase database)
    {
        using var command = database.Command("SELECT value FROM read_probe");
        return (long)command.ExecuteScalar()!;
    }

    private static void SetValue(PicerDatabase database, long value)
    {
        using var command = database.Command("UPDATE read_probe SET value=$value");
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
