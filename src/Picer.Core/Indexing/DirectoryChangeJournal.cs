using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Picer.Core.Indexing;

public sealed record DirectoryJournalCheckpoint(uint VolumeSerial, ulong RootReference, ulong JournalId, long NextUsn, long FirstUsn, long LowestValidUsn);
public sealed record DirectoryJournalDelta(bool Reliable, IReadOnlyList<string> DirtyDirectories, IReadOnlyList<string> DirtyFiles);

/// <summary>Only a complete journal range may authorize persistent listing reuse.</summary>
public interface IDirectoryChangeJournal
{
    DirectoryJournalCheckpoint? Capture(string root);
    ulong? GetReference(string path);
    DirectoryJournalDelta ReadChanges(string root, DirectoryJournalCheckpoint from, DirectoryJournalCheckpoint to,
        IReadOnlyDictionary<string, ulong> directories, IReadOnlyDictionary<string, ulong> files, CancellationToken cancellationToken);
}

/// <summary>Read-only NTFS USN access. Never creates, resizes or deletes a volume journal.</summary>
public sealed class NtfsDirectoryChangeJournal : IDirectoryChangeJournal
{
    private const uint QueryJournal = 0x000900F4;
    private const uint ReadJournal = 0x000900BB;
    private static readonly DirectoryJournalDelta Unknown = new(false, [], []);

    public DirectoryJournalCheckpoint? Capture(string root)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var drive = Path.GetPathRoot(Path.GetFullPath(root));
            if (drive is not { Length: 3 } || drive[1] != ':') return null;
            var format = new StringBuilder(32);
            if (!GetVolumeInformation(drive, null, 0, out var serial, out _, out _, format, format.Capacity) || !format.ToString().Equals("NTFS", StringComparison.OrdinalIgnoreCase)) return null;
            using var directory = OpenPath(root);
            if (directory.IsInvalid || !GetFileInformationByHandle(directory, out var identity) || identity.VolumeSerialNumber != serial) return null;
            using var volume = OpenVolume(drive);
            if (volume.IsInvalid) return null;
            var buffer = new byte[128];
            if (!DeviceIoControl(volume, QueryJournal, null, 0, buffer, buffer.Length, out var returned, IntPtr.Zero) || returned < 56) return null;
            return new(serial, ((ulong)identity.FileIndexHigh << 32) | identity.FileIndexLow,
                BinaryPrimitives.ReadUInt64LittleEndian(buffer), BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(16)),
                BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(8)), BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(24)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception) { return null; }
    }

    public ulong? GetReference(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var handle = OpenPath(path);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var identity) || (identity.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0) return null;
        return ((ulong)identity.FileIndexHigh << 32) | identity.FileIndexLow;
    }

    public DirectoryJournalDelta ReadChanges(string root, DirectoryJournalCheckpoint from, DirectoryJournalCheckpoint to,
        IReadOnlyDictionary<string, ulong> directories, IReadOnlyDictionary<string, ulong> files, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !IsContinuous(from, to)) return Unknown;
        var byDirectory = directories.GroupBy(pair => pair.Value).ToDictionary(group => group.Key, group => group.Select(pair => pair.Key).ToArray());
        var byFile = files.GroupBy(pair => pair.Value).ToDictionary(group => group.Key, group => group.Select(pair => pair.Key).ToArray());
        var changedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var volume = OpenVolume(Path.GetPathRoot(Path.GetFullPath(root))!);
        if (volume.IsInvalid) return Unknown;
        var cursor = from.NextUsn;
        var input = new byte[40];
        var buffer = new byte[64 * 1024];
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var bytes = 0L;
        while (cursor < to.NextUsn)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A very old checkpoint should fall back rather than replay an unbounded
            // system-wide history while the user waits for a directory preview.
            if (watch.Elapsed > TimeSpan.FromSeconds(10) || bytes > 64L * 1024 * 1024) return Unknown;
            BinaryPrimitives.WriteInt64LittleEndian(input, cursor);
            BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), uint.MaxValue);
            BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), from.JournalId);
            if (!DeviceIoControl(volume, ReadJournal, input, input.Length, buffer, buffer.Length, out var returned, IntPtr.Zero) || returned < 8) return Unknown;
            bytes += returned;
            var next = BinaryPrimitives.ReadInt64LittleEndian(buffer);
            if (next <= cursor) return Unknown;
            if (!ReadRecords(buffer.AsSpan(8, returned - 8), to.NextUsn, byDirectory, byFile, changedDirectories, changedFiles)) return Unknown;
            cursor = next;
        }
        return new(true, changedDirectories.ToArray(), changedFiles.ToArray());
    }

    public static bool IsContinuous(DirectoryJournalCheckpoint from, DirectoryJournalCheckpoint to) =>
        from.VolumeSerial == to.VolumeSerial && from.RootReference == to.RootReference && from.JournalId == to.JournalId
        && from.NextUsn >= Math.Max(to.FirstUsn, to.LowestValidUsn) && from.NextUsn <= to.NextUsn;

    internal static bool ReadRecords(ReadOnlySpan<byte> records, long stopUsn,
        IReadOnlyDictionary<ulong, string[]> directories, IReadOnlyDictionary<ulong, string[]> files,
        HashSet<string> changedDirectories, HashSet<string> changedFiles)
    {
        var offset = 0;
        while (offset < records.Length)
        {
            if (records.Length - offset < 60) return false;
            var record = records[offset..];
            var length = BinaryPrimitives.ReadUInt32LittleEndian(record);
            if (length < 60 || length > record.Length || (length & 7) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(record[4..]) != 2) return false;
            record = record[..(int)length];
            var usn = BinaryPrimitives.ReadInt64LittleEndian(record[24..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[56..]);
            var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[58..]);
            if (nameOffset < 60 || (nameLength & 1) != 0 || nameOffset + nameLength > length) return false;
            if (usn < stopUsn)
            {
                var reference = BinaryPrimitives.ReadUInt64LittleEndian(record[8..]);
                var parent = BinaryPrimitives.ReadUInt64LittleEndian(record[16..]);
                var name = Encoding.Unicode.GetString(record.Slice(nameOffset, nameLength));
                if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(['/', '\\', '\0', ':']) >= 0) return false;
                if (directories.TryGetValue(parent, out var parents))
                    foreach (var directory in parents)
                    {
                        changedDirectories.Add(directory);
                        changedFiles.Add(Path.Combine(directory, name));
                    }
                if (directories.TryGetValue(reference, out var moved))
                    foreach (var directory in moved) { changedDirectories.Add(directory); if (Path.GetDirectoryName(directory) is { } ancestor) changedDirectories.Add(ancestor); }
                // A write via a hard link outside the selected tree may report an
                // unrelated parent, but its file reference still invalidates our copy.
                if (files.TryGetValue(reference, out var aliases))
                    foreach (var file in aliases) { changedFiles.Add(file); if (Path.GetDirectoryName(file) is { } directory) changedDirectories.Add(directory); }
            }
            offset += (int)length;
        }
        return true;
    }

    private static SafeFileHandle OpenVolume(string drive) => CreateFile("\\\\.\\" + drive[..2], 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
    private static SafeFileHandle OpenPath(string path) => CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileIdentity information);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string root, StringBuilder? name, int nameSize, out uint serial, out uint maximumComponentLength, out uint flags, StringBuilder format, int formatSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[]? input, int inputLength, byte[] output, int outputLength, out int returned, IntPtr overlapped);
}
