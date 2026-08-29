using Picer.Core.Storage;

namespace Picer.Tests.Stubs;

/// <summary>
/// Storage provider backed by actual physical files on disk.
/// Used in integration tests where files are written to temp directories.
/// </summary>
public sealed class DiskBackedStorageProvider : IStorageProvider
{
    public string Scheme => "disk";

    public IEnumerable<StorageFileInfo> EnumerateFiles(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> files = Array.Empty<string>();
            IEnumerable<string> dirs = Array.Empty<string>();
            try
            {
                files = Directory.EnumerateFiles(dir);
                dirs = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (var d in dirs)
                stack.Push(d);

            foreach (var f in files)
            {
                FileInfo info;
                try
                {
                    info = new FileInfo(f);
                    if (!info.Exists)
                        continue;
                }
                catch
                {
                    continue;
                }

                yield return new StorageFileInfo
                {
                    Key = info.FullName,
                    Size = info.Length,
                    ModifiedUtc = info.LastWriteTimeUtc
                };
            }
        }
    }

    public bool Exists(string key) => File.Exists(key);

    public Stream OpenRead(string key) => File.OpenRead(key);

    public StorageFileInfo? GetInfo(string key)
    {
        var info = new FileInfo(key);
        if (!info.Exists)
            return null;
        return new StorageFileInfo
        {
            Key = info.FullName,
            Size = info.Length,
            ModifiedUtc = info.LastWriteTimeUtc
        };
    }

    public Task<IEnumerable<StorageFileInfo>> EnumerateFilesAsync(string root, CancellationToken ct = default)
        => Task.FromResult(EnumerateFiles(root));

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
        => Task.FromResult(Exists(key));

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
        => Task.FromResult(OpenRead(key));

    public Task<StorageFileInfo?> GetInfoAsync(string key, CancellationToken ct = default)
        => Task.FromResult(GetInfo(key));
}
