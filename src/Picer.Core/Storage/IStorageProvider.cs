namespace Picer.Core.Storage;

public sealed class StorageFileInfo
{
    public required string Key { get; init; }
    public long Size { get; init; }
    public DateTime ModifiedUtc { get; init; }
    public DateTime? CreatedUtc { get; init; }
    public string? ETag { get; init; }
}

public interface IStorageProvider
{
    string Scheme { get; }
    IEnumerable<StorageFileInfo> EnumerateFiles(string root);
    bool Exists(string key);
    Stream OpenRead(string key);
    StorageFileInfo? GetInfo(string key);

    Task<IEnumerable<StorageFileInfo>> EnumerateFilesAsync(string root, CancellationToken ct = default);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct = default);
    Task<StorageFileInfo?> GetInfoAsync(string key, CancellationToken ct = default);
}
