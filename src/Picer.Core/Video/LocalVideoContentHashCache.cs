using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;
using Picer.Core.Indexing;

namespace Picer.Core.Video;

/// <summary>Bounded hashes, reusable only with a live NTFS journal and a per-file USN identity.</summary>
public sealed class LocalVideoContentHashCache
{
    internal readonly record struct Signature(ulong Volume, ulong Low, ulong High, long ChangeTime, long Length,
        ulong JournalId, long FileUsn);
    private sealed record Entry(Signature Signature, string Hash, long LastUsed);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<Stream, CancellationToken, Task<byte[]>> _hash;
    private readonly Func<string, FileStream, Signature?> _signature;
    private readonly int _capacity;
    private long _generation;
    private long _access;
    public static LocalVideoContentHashCache Shared { get; } = new();

    internal LocalVideoContentHashCache(Func<Stream, CancellationToken, Task<byte[]>>? hash = null, int capacity = 128,
        Func<string, FileStream, Signature?>? signature = null)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _hash = hash ?? ((stream, token) => SHA256.HashDataAsync(stream, token).AsTask());
        _capacity = capacity;
        _signature = signature ?? ReadSignature;
    }

    public static void ForgetFile(string path) => Shared.Invalidate(path);
    internal void Invalidate(string path)
    {
        lock (_gate) { _generation++; _entries.Remove(Path.GetFullPath(path)); }
    }
    internal int Count { get { lock (_gate) return _entries.Count; } }

    internal async Task<string> GetHashAsync(string path, CancellationToken token)
    {
        path = Path.GetFullPath(path);
        token.ThrowIfCancellationRequested();
        long generation;
        lock (_gate) generation = _generation;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.SequentialScan);
        // DeviceIoControl below uses the same synchronous handle. HashDataAsync
        // remains asynchronous/cancellable; passing a null OVERLAPPED to an
        // overlapped handle would make the USN query unreliable.
        var signature = _signature(path, input);
        lock (_gate)
        {
            if (generation == _generation && signature is { } current && _entries.TryGetValue(path, out var cached)
                && cached.Signature == current)
            {
                _entries[path] = cached with { LastUsed = ++_access };
                return cached.Hash;
            }
        }
        var result = Convert.ToHexString(await _hash(input, token).ConfigureAwait(false)).ToLowerInvariant();
        var after = _signature(path, input);
        if (signature is not null && signature != after) throw new IOException("video_changed_during_cache_hash");
        lock (_gate)
        {
            if (generation == _generation && signature is not null && after is { } verified)
            {
                if (!_entries.ContainsKey(path) && _entries.Count >= _capacity) _entries.Remove(_entries.MinBy(pair => pair.Value.LastUsed).Key);
                _entries[path] = new(verified, result, ++_access);
            }
        }
        return result;
    }

    internal static Signature? ReadSignature(string path, FileStream input)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var root = Path.GetPathRoot(path);
            if (root is not { Length: 3 } || root[1] != ':') return null;
            var drive = new DriveInfo(root);
            if (drive.DriveType == DriveType.Network || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) return null;
            // File USNs alone may survive a disabled/recreated journal. Require
            // an active readable journal and include its generation in the key.
            var journal = new NtfsDirectoryChangeJournal().Capture(Path.GetDirectoryName(path)!);
            if (journal is null) return null;
            if (!GetBasicInformation(input.SafeFileHandle, 0, out var basic, (uint)Marshal.SizeOf<BasicInformation>())
                || !GetIdentityInformation(input.SafeFileHandle, 18, out var identity, (uint)Marshal.SizeOf<IdentityInformation>())
                || basic.ChangeTime <= 0 || (basic.Attributes & (uint)FileAttributes.ReparsePoint) != 0) return null;
            var buffer = new byte[1024];
            if (!ReadFileUsn(input.SafeFileHandle, 0x000900EB, IntPtr.Zero, 0, buffer, buffer.Length, out var returned, IntPtr.Zero)) return null;
            var usn = ParseFileUsn(buffer.AsSpan(0, returned), identity.Low, identity.High);
            if (usn is null || usn >= journal.NextUsn) return null;
            return new(identity.Volume, identity.Low, identity.High, basic.ChangeTime, input.Length, journal.JournalId, usn.Value);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    internal static long? ParseFileUsn(ReadOnlySpan<byte> record, ulong expectedLow, ulong expectedHigh)
    {
        if (record.Length < 60 || BinaryPrimitives.ReadUInt32LittleEndian(record) < 60
            || BinaryPrimitives.ReadUInt32LittleEndian(record) > record.Length
            || BinaryPrimitives.ReadUInt16LittleEndian(record[4..]) != 2
            || expectedHigh != 0 || BinaryPrimitives.ReadUInt64LittleEndian(record[8..]) != expectedLow) return null;
        var usn = BinaryPrimitives.ReadInt64LittleEndian(record[24..]);
        return usn > 0 ? usn : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation
    {
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        public uint Attributes;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IdentityInformation { public ulong Volume, Low, High; }
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetBasicInformation(SafeFileHandle handle, int informationClass, out BasicInformation information, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIdentityInformation(SafeFileHandle handle, int informationClass, out IdentityInformation information, uint size);
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFileUsn(SafeFileHandle handle, uint control, IntPtr input, int inputLength,
        byte[] output, int outputLength, out int returned, IntPtr overlapped);
}
