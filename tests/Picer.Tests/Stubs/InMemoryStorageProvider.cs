using Picer.Core.Storage;

namespace Picer.Tests.Stubs;

public sealed class InMemoryStorageProvider : IStorageProvider
{
    // key -> (content bytes, creation time as file system timestamp)
    private readonly Dictionary<string, (byte[] content, DateTime utcNow)> _files = new(StringComparer.OrdinalIgnoreCase);

    public string Scheme => "inmemory";

    public IEnumerable<StorageFileInfo> EnumerateFiles(string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
        foreach (var kvp in _files)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(kvp.Key);
            }
            catch
            {
                continue;
            }
            if (fullPath.StartsWith(normalizedRoot))
            {
                yield return new StorageFileInfo
                {
                    Key = kvp.Key,
                    Size = kvp.Value.content.LongLength,
                    ModifiedUtc = kvp.Value.utcNow
                };
            }
        }
    }

    public bool Exists(string key) => _files.ContainsKey(key);

    public Stream OpenRead(string key) => new MemoryStream(_files[key].content);

    public StorageFileInfo? GetInfo(string key)
    {
        if (_files.TryGetValue(key, out var data))
            return new StorageFileInfo { Key = key, Size = data.content.LongLength, ModifiedUtc = data.utcNow };
        return null;
    }

    public Task<IEnumerable<StorageFileInfo>> EnumerateFilesAsync(string root, CancellationToken ct = default)
        => Task.FromResult(EnumerateFiles(root));

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
        => Task.FromResult(Exists(key));

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
        => Task.FromResult(OpenRead(key));

    public Task<StorageFileInfo?> GetInfoAsync(string key, CancellationToken ct = default)
        => Task.FromResult(GetInfo(key));

    // --- Test helpers ---

    public void AddFile(string key, byte[] content) => _files[key] = (content, DateTime.UtcNow);

    public void RemoveFile(string key) => _files.Remove(key);

    public void UpdateFile(string key, byte[] content)
    {
        if (_files.TryGetValue(key, out var entry))
            _files[key] = (content, entry.utcNow); // preserve original mtime
    }
}
