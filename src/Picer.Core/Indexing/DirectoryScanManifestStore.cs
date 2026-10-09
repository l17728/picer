using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Picer.Core.Indexing;

internal sealed record DirectoryScanManifest(int Version, string Root, DirectoryJournalCheckpoint Checkpoint,
    DateTime SavedUtc, Dictionary<string, DirectoryMediaChangeTracker.DirectoryListing> Directories, string[] PendingFiles,
    Dictionary<string, string> VerifiedHashes, string Generation);

/// <summary>Disposable acceleration data, bounded and atomically replaced; never stores AI/person data.</summary>
internal sealed class DirectoryScanManifestStore(string directory)
{
    private const long MaximumManifestBytes = 64L * 1024 * 1024;
    private readonly string _directory = Path.GetFullPath(directory);
    private string EpochPath => Path.Combine(_directory, "purge.epoch");
    public string Generation()
    {
        try { return File.Exists(EpochPath) ? File.ReadAllText(EpochPath) : ""; }
        catch (IOException) { return "unavailable"; }
        catch (UnauthorizedAccessException) { return "unavailable"; }
    }
    private Mutex MutationLock()
    {
        var mutex = new Mutex(false, "Picer.ScanManifest." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_directory.ToUpperInvariant()))));
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { /* Previous owner exited; this thread owns the mutex. */ }
        return mutex;
    }
    private string PathFor(string root) => Path.Combine(_directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))).ToLowerInvariant() + ".scan");

    public DirectoryScanManifest? Load(string root)
    {
        using var mutex = MutationLock();
        try
        {
            RemoveAbandonedTemporaryFiles();
            var path = PathFor(root);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is < 33 or > MaximumManifestBytes) return null;
            var bytes = File.ReadAllBytes(path);
            if (!CryptographicOperations.FixedTimeEquals(bytes.AsSpan(0, 32), SHA256.HashData(bytes.AsSpan(32)))) return null;
            var manifest = JsonSerializer.Deserialize<DirectoryScanManifest>(bytes.AsSpan(32));
            if (manifest is null || manifest.Version != 1 || manifest.Generation != Generation() || !string.Equals(root, manifest.Root, StringComparison.OrdinalIgnoreCase)
                || manifest.Checkpoint is null || manifest.Directories is null || manifest.PendingFiles is null || manifest.VerifiedHashes is null
                || manifest.Directories.Count > 100000 || !manifest.Directories.ContainsKey(root)
                || manifest.PendingFiles.Any(path => !DirectoryMediaImport.Contains(root, path))) return null;
            if (manifest.VerifiedHashes.Any(pair => !DirectoryMediaImport.Contains(root, pair.Key) || pair.Value is not { Length: 64 } || !pair.Value.All(Uri.IsHexDigit))) return null;
            foreach (var (pathKey, value) in manifest.Directories)
            {
                if (value is null || value.Files is null || value.Children is null || !DirectoryMediaImport.Contains(root, pathKey) || value.DirectoryReference == 0 || value.FileReferences is null
                    || value.Files.Any(file => !SameParent(pathKey, file) || !value.FileReferences.ContainsKey(file))
                    || value.Children.Any(child => !SameParent(pathKey, child))) return null;
            }
            return manifest;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { return null; }
        finally { mutex.ReleaseMutex(); }
    }

    private static bool SameParent(string parent, string child) => DirectoryMediaImport.Contains(parent, child)
        && string.Equals(Path.GetDirectoryName(child), parent, StringComparison.OrdinalIgnoreCase);

    public void Save(DirectoryScanManifest manifest)
    {
        string? temporary = null;
        using var mutex = MutationLock();
        try
        {
            RemoveAbandonedTemporaryFiles();
            if (manifest.Generation != Generation()) return;
            if (manifest.Directories.Count > 100000) return;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
            if (bytes.LongLength + 32 > MaximumManifestBytes) return;
            Directory.CreateDirectory(_directory);
            var destination = PathFor(manifest.Root);
            temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(SHA256.HashData(bytes)); output.Write(bytes); output.Flush(flushToDisk: true); }
            File.Move(temporary, destination, true);
            var files = Directory.EnumerateFiles(_directory, "*.scan").Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
            long size = 0;
            for (var index = 0; index < files.Length; index++)
            {
                size += files[index].Length;
                if (index >= 16 || size > 256L * 1024 * 1024) File.Delete(files[index].FullName);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { /* Acceleration data must never fail the media scan. */ }
        finally { if (temporary is not null) TryDelete(temporary); mutex.ReleaseMutex(); }
    }

    public void Forget(string root)
    {
        using var mutex = MutationLock();
        try { RotateEpoch(); RemoveAbandonedTemporaryFiles(); File.Delete(PathFor(root)); }
        finally { mutex.ReleaseMutex(); }
    }
    private void RotateEpoch()
    {
        Directory.CreateDirectory(_directory);
        var temporary = EpochPath + ".tmp";
        File.WriteAllText(temporary, Guid.NewGuid().ToString("N"));
        File.Move(temporary, EpochPath, true);
    }
    public void ForgetFile(string path)
    {
        using var mutex = MutationLock();
        try
        {
        RotateEpoch(); // Also blocks an in-flight first save whose root is not on disk yet.
        RemoveAbandonedTemporaryFiles();
        foreach (var manifestPath in Directory.EnumerateFiles(_directory, "*.scan"))
        {
            try
            {
                if (new FileInfo(manifestPath).Length is < 33 or > MaximumManifestBytes) { File.Delete(manifestPath); continue; }
                var bytes = File.ReadAllBytes(manifestPath);
                var manifest = JsonSerializer.Deserialize<DirectoryScanManifest>(bytes.AsSpan(32));
                if (manifest is null || DirectoryMediaImport.Contains(manifest.Root, path)) File.Delete(manifestPath);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            { File.Delete(manifestPath); }
        }
        }
        finally { mutex.ReleaseMutex(); }
    }

    // All manifest/epoch writers hold the same cross-process mutex. While it is
    // held, a correctly named temporary file can only be left by a failed write
    // or terminated process, regardless of its age or last-write timestamp.
    private void RemoveAbandonedTemporaryFiles()
    {
        if (!Directory.Exists(_directory)) return;
        foreach (var path in Directory.EnumerateFiles(_directory, "*.tmp"))
        {
            var name = Path.GetFileName(path);
            var parts = name.Split('.');
            if (name.Equals("purge.epoch.tmp", StringComparison.OrdinalIgnoreCase)
                || (parts.Length == 4 && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit)
                    && parts[1].Equals("scan", StringComparison.OrdinalIgnoreCase)
                    && parts[2].Length == 32 && parts[2].All(Uri.IsHexDigit)
                    && parts[3].Equals("tmp", StringComparison.OrdinalIgnoreCase)))
                File.Delete(path);
        }
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
