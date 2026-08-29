using Picer.App;
using Picer.Core.Data;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Picer.E2E;

public sealed class DataProtectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"picer-protection-{Guid.NewGuid():N}");
    [Fact]
    public async Task Backup_IsConsistentVersionedAndSelfValidating()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "directory-classification.json"), "{}");
        using var database = new PicerDatabase(Path.Combine(_root, "picer.db"));
        database.AddSource(Path.Combine(_root, "photos"));
        var archive = Path.Combine(_root, "backup.picer-backup");
        var service = new DataProtectionService(database, _root);
        await service.CreateBackupAsync(archive);
        var manifest = DataProtectionService.ValidateBackup(archive);
        Assert.Equal(1, manifest.FormatVersion);
        Assert.Equal(19, manifest.DatabaseVersion);
        Assert.Contains("picer.db", manifest.Entries);
        Assert.Contains("directory-classification.json", manifest.Entries);
    }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
