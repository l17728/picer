using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Picer.Core.Video;

/// <summary>Bounded process-local hashes, reusable only with a trusted NTFS change-time/file identity.</summary>
public sealed class LocalVideoContentHashCache
{
    private readonly record struct Signature(ulong Volume, ulong Low, ulong High, long ChangeTime, long Length);
    private sealed record Entry(Signature Signature, string Hash, long LastUsed);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<Stream, CancellationToken, Task<byte[]>> _hash;
    private readonly int _capacity;
    private long _generation;
    private long _access;
    public static LocalVideoContentHashCache Shared { get; } = new();

    internal LocalVideoContentHashCache(Func<Stream, CancellationToken, Task<byte[]>>? hash = null, int capacity = 128)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _hash = hash ?? ((stream, token) => SHA256.HashDataAsync(stream, token).AsTask());
        _capacity = capacity;
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
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var signature = ReadSignature(path, input);
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
        var after = ReadSignature(path, input);
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

    private static Signature? ReadSignature(string path, FileStream input)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var root = Path.GetPathRoot(path);
            if (root is not { Length: 3 } || root[1] != ':') return null;
            var drive = new DriveInfo(root);
            if (drive.DriveType == DriveType.Network || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) return null;
            if (!GetBasicInformation(input.SafeFileHandle, 0, out var basic, (uint)Marshal.SizeOf<BasicInformation>())
                || !GetIdentityInformation(input.SafeFileHandle, 18, out var identity, (uint)Marshal.SizeOf<IdentityInformation>())
                || basic.ChangeTime <= 0 || (basic.Attributes & (uint)FileAttributes.ReparsePoint) != 0) return null;
            return new(identity.Volume, identity.Low, identity.High, basic.ChangeTime, input.Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
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
}
