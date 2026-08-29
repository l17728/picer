namespace Picer.Core.Storage;

public sealed class LocalStorageProvider : IStorageProvider
{
    public string Scheme => "local";

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
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
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
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                yield return new StorageFileInfo
                {
                    Key = info.FullName,
                    Size = info.Length,
                    ModifiedUtc = info.LastWriteTimeUtc
                    ,CreatedUtc = info.CreationTimeUtc
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
            ,CreatedUtc = info.CreationTimeUtc
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
