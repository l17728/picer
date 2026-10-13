using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Picer.Core.Data;

/// <summary>Closed-file snapshots and recoverable publication of a migrated staging database.</summary>
internal static class DatabaseMigrationSafety
{
    internal static void CopyClosed(string source, string destination)
    {
        source = Path.GetFullPath(source); destination = Path.GetFullPath(destination);
        if (!File.Exists(source) || string.Equals(source, destination, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("closed_snapshot_requires_distinct_existing_source");
        using var startup = Enter(source);
        using var access = ExclusiveAccess(source);
        SqliteConnection.ClearAllPools();
        using var files = ClosedFiles.Open(source);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        files.CopyTo(destination);
    }

    private sealed record Pending(string BackupDirectory);

    internal static IDisposable Enter(string path)
    {
        var identity = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) identity = identity.ToUpperInvariant();
        var mutex = new Mutex(false, "PicerDatabaseStartup-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
        try { if (!mutex.WaitOne(TimeSpan.FromSeconds(30))) throw new IOException("database_startup_busy_close_other_instances"); }
        catch (AbandonedMutexException) { }
        catch { mutex.Dispose(); throw; }
        return new MutexLease(mutex);
    }

    internal static FileStream OpenLifetimeLease(string path) => new(path + ".access", FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);

    internal static void Prepare(string path, int targetVersion, Action<string> migrate)
    {
        path = Path.GetFullPath(path);
        var pendingPath = path + ".migration.pending.json";
        if (File.Exists(pendingPath)) Recover(path, pendingPath);
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return;
        var version = ReadVersionWithoutSqlite(path);
        if (version > targetVersion) throw new InvalidDataException("database_schema_newer_than_application");
        if (version == targetVersion) return;

        using var access = ExclusiveAccess(path);
        SqliteConnection.ClearAllPools(); // Dispose pooled handles; active connections still prevent the closed-file lock.
        using var originals = ClosedFiles.Open(path);
        // Re-read after all original files are exclusively held, never trust the preliminary probe.
        version = ReadVersion(originals.Main, originals.Wal);
        if (version > targetVersion) throw new InvalidDataException("database_schema_newer_than_application");
        if (version == targetVersion) return;
        var backup = Path.Combine(path + ".migration-backups", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        originals.CopyTo(Path.Combine(backup, Path.GetFileName(path)));
        var staging = Path.Combine(Path.GetDirectoryName(path)!, ".picer-migration-" + Guid.NewGuid().ToString("N") + ".db");
        CopySnapshot(Path.Combine(backup, Path.GetFileName(path)), staging);
        try
        {
            migrate(staging);
            var temporaryMarker = pendingPath + ".tmp";
            using (var marker = new FileStream(temporaryMarker, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                marker.Write(JsonSerializer.SerializeToUtf8Bytes(new Pending(backup)));
                marker.Flush(true);
            }
            File.Move(temporaryMarker, pendingPath, true);
            // The marker is durable before touching the original trio. Any interrupted
            // publication is restored from the closed-file snapshot on the next startup.
            foreach (var suffix in new[] { "-wal", "-shm" }) File.Delete(path + suffix);
            File.Replace(staging, path, null);
            File.Delete(pendingPath);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", ".access" })
                try { File.Delete(staging + suffix); } catch (IOException) { }
        }
    }

    private static FileStream ExclusiveAccess(string path)
    {
        try { return new FileStream(path + ".access", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) { throw new IOException("database_upgrade_requires_closed_instances", exception); }
    }

    private static void Recover(string path, string pendingPath)
    {
        using var access = ExclusiveAccess(path);
        SqliteConnection.ClearAllPools();
        var pending = JsonSerializer.Deserialize<Pending>(File.ReadAllText(pendingPath)) ?? throw new InvalidDataException("database_migration_recovery_marker_invalid");
        var backupRoot = Path.GetFullPath(path + ".migration-backups") + Path.DirectorySeparatorChar;
        var backup = Path.GetFullPath(pending.BackupDirectory);
        if (!backup.StartsWith(backupRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("database_migration_recovery_backup_outside_root");
        var source = Path.Combine(backup, Path.GetFileName(path));
        if (!File.Exists(source)) throw new InvalidDataException("database_migration_recovery_backup_missing");
        using var originals = ClosedFiles.Open(path);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(source + suffix))
            {
                var temporary = path + suffix + ".recovering";
                File.Copy(source + suffix, temporary, true);
                if (File.Exists(path + suffix)) File.Replace(temporary, path + suffix, null);
                else File.Move(temporary, path + suffix);
            }
            else File.Delete(path + suffix);
        }
        File.Delete(pendingPath);
    }

    private static void CopySnapshot(string source, string destination)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(source + suffix)) File.Copy(source + suffix, destination + suffix);
    }

    private static int ReadVersionWithoutSqlite(string path)
    {
        using var main = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var wal = File.Exists(path + "-wal") ? new FileStream(path + "-wal", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete) : null;
        return ReadVersion(main, wal);
    }

    // Read SQLite page-one user_version and committed WAL page-one frames without opening
    // SQLite itself: a normal read-only SQLite connection can still create/recover SHM.
    private static int ReadVersion(FileStream main, FileStream? wal)
    {
        Span<byte> header = stackalloc byte[100];
        main.Position = 0; main.ReadExactly(header);
        if (!header[..16].SequenceEqual("SQLite format 3\0"u8)) throw new InvalidDataException("invalid_database_header");
        var version = BinaryPrimitives.ReadInt32BigEndian(header[60..64]);
        if (wal is null || wal.Length < 32) return version;
        Span<byte> walHeader = stackalloc byte[32]; wal.Position = 0; wal.ReadExactly(walHeader);
        var magic = BinaryPrimitives.ReadUInt32BigEndian(walHeader[..4]);
        if (magic is not (0x377f0682 or 0x377f0683) || BinaryPrimitives.ReadUInt32BigEndian(walHeader[4..8]) != 3007000)
            throw new InvalidDataException("invalid_database_wal_header");
        var littleEndian = magic == 0x377f0682;
        uint checksum0 = 0, checksum1 = 0;
        Checksum(walHeader[..24], littleEndian, ref checksum0, ref checksum1);
        if (checksum0 != BinaryPrimitives.ReadUInt32BigEndian(walHeader[24..28]) ||
            checksum1 != BinaryPrimitives.ReadUInt32BigEndian(walHeader[28..32]))
            throw new InvalidDataException("invalid_database_wal_checksum");
        var pageSize = BinaryPrimitives.ReadUInt32BigEndian(walHeader[8..12]);
        if (pageSize is < 512 or > 65536 || (pageSize & (pageSize - 1)) != 0) throw new InvalidDataException("invalid_database_wal_header");
        Span<byte> frame = stackalloc byte[24];
        var page = new byte[pageSize];
        var length = wal.Length;
        int? pendingVersion = null;
        for (long offset = 32; offset + 24 + pageSize <= length; offset += 24 + pageSize)
        {
            wal.Position = offset; wal.ReadExactly(frame);
            if (!frame[8..16].SequenceEqual(walHeader[16..24])) break;
            wal.ReadExactly(page);
            Checksum(frame[..8], littleEndian, ref checksum0, ref checksum1);
            Checksum(page, littleEndian, ref checksum0, ref checksum1);
            // As in SQLite recovery, the first invalid frame ends the valid WAL
            // prefix. An unfinished/torn tail must never supply a commit marker.
            if (checksum0 != BinaryPrimitives.ReadUInt32BigEndian(frame[16..20]) ||
                checksum1 != BinaryPrimitives.ReadUInt32BigEndian(frame[20..24])) break;
            if (BinaryPrimitives.ReadUInt32BigEndian(frame[..4]) == 1)
            {
                pendingVersion = BinaryPrimitives.ReadInt32BigEndian(page.AsSpan(60, 4));
            }
            if (BinaryPrimitives.ReadUInt32BigEndian(frame[4..8]) != 0 && pendingVersion is { } committed)
            { version = committed; pendingVersion = null; }
        }
        return version;
    }

    // SQLite file format 4.2: https://www.sqlite.org/fileformat2.html#checksum_algorithm
    private static void Checksum(ReadOnlySpan<byte> bytes, bool littleEndian, ref uint first, ref uint second)
    {
        for (var offset = 0; offset < bytes.Length; offset += 8)
        {
            var left = littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]) : BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            var right = littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]) : BinaryPrimitives.ReadUInt32BigEndian(bytes[(offset + 4)..]);
            first = unchecked(first + left + second);
            second = unchecked(second + right + first);
        }
    }

    private sealed class ClosedFiles : IDisposable
    {
        private readonly Dictionary<string, FileStream> _files = new();
        public FileStream Main => _files[""];
        public FileStream? Wal => _files.GetValueOrDefault("-wal");
        public static ClosedFiles Open(string path)
        {
            var result = new ClosedFiles();
            try
            {
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(path + suffix))
                    {
                        // ReplaceFile needs read/delete sharing on its target. A
                        // whole-file byte-range lock keeps SQLite readers/writers
                        // out without releasing the guard during atomic replacement.
                        var stream = new FileStream(path + suffix, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
                        result._files.Add(suffix, stream);
                        // POSIX byte-range locks also conflict with SQLite's native locks;
                        // FileShare alone only coordinates managed callers on Unix.
                        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows()) stream.Lock(0, long.MaxValue);
                    }
                return result;
            }
            catch (IOException exception)
            { result.Dispose(); throw new IOException("database_upgrade_requires_closed_database_wal_shm", exception); }
        }
        public void CopyTo(string path)
        {
            foreach (var (suffix, stream) in _files)
            {
                stream.Position = 0;
                using var copy = new FileStream(path + suffix, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.CopyTo(copy); copy.Flush(true);
            }
        }
        public void Dispose() { foreach (var stream in _files.Values) stream.Dispose(); }
    }

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}
