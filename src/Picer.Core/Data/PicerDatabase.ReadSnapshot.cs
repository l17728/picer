namespace Picer.Core.Data;

public sealed partial class PicerDatabase
{
    private Microsoft.Data.Sqlite.SqliteTransaction? _readSnapshotTransaction;
    public bool SupportsIndependentReadSnapshots => !_isInMemory;

    /// <summary>File-backed read connection with its own migration lifetime lease.
    /// Never migrates, changes journal mode, or shares the writer's transaction.</summary>
    public PicerDatabase OpenReadOnlyConnection()
    {
        if (_isInMemory) throw new NotSupportedException("read_snapshot_requires_file_backed_database");
        return new(DatabasePath, staging: false, independent: true, readOnly: true);
    }

    /// <summary>Materialize all results inside the callback; do not return readers or lazy sequences.</summary>
    public T ReadSnapshot<T>(Func<PicerDatabase, T> read, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = OpenReadOnlyConnection();
        using var snapshot = connection.BeginReadTransaction();
        connection._readSnapshotTransaction = snapshot;
        // Establish one SQLite snapshot before any callback queries.
        using (var pin = connection.Command("SELECT user_version FROM pragma_user_version")) pin.ExecuteScalar();
        cancellationToken.ThrowIfCancellationRequested();
        var result = read(connection);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
