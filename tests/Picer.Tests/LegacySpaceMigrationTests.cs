using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Picer.Core.Data;
using Picer.Core.Models;
using Picer.Core.Video;
using Picer.IndexService.Identity;
using Xunit;

namespace Picer.Tests;

public sealed class LegacySpaceMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-legacy-space-" + Guid.NewGuid().ToString("N"));
    private const string Password = "migration-test-password-123!";

    [Fact]
    public void MigrationPreservesOriginalAndIdsAndPublishesOnlyVerifiedSpace()
    {
        var path = Path.Combine(_root, "legacy", "picer.db");
        using (var database = new PicerDatabase(path))
        {
            var source = database.AddSource(@"C:\Photos");
            database.UpsertMirroredImage(new ImageRecord { Id = 7, SourceId = source.Id, Path = @"C:\Photos\trip.jpg",
                FileSize = 50, ModifiedUtc = DateTime.UtcNow, IndexedUtc = DateTime.UtcNow, Sha256 = new string('a', 64) });
            new VideoRepository(database).UpsertRemote(new(18, source.Id, @"C:\Photos\trip.mp4", 500,
                DateTime.UtcNow, 10, 640, 480, null, null, "active") { Sha256 = new string('b', 64) }, source.Id, true);
        }
        SqliteConnection.ClearAllPools();
        var originalHash = Hash(path);
        var accounts = new AccountStore(Path.Combine(_root, "accounts"));
        var guard = new RestoreGuard(Path.Combine(_root, "guard"), accounts.ServiceId, true);
        var migrator = new LegacySpaceMigrator(accounts, guard);
        var result = migrator.Migrate(path, "old-owner", Password, "原有照片");
        Assert.Equal(originalHash, Hash(path));
        Assert.Equal(originalHash, Hash(Path.Combine(result.BackupDirectory, "picer.db")));
        Assert.Equal(result.AccountId, accounts.Authenticate("old-owner", Password));
        Assert.Equal(result.SpaceId, Assert.Single(accounts.ListSpaces(result.AccountId)).Id);
        Assert.Equal("complete", accounts.GetLegacyMigration()!.State);
        using (var migrated = new PicerDatabase(Path.Combine(accounts.SpaceRoot(result.SpaceId), "picer.db")))
        {
            Assert.EndsWith(@"\C$\Photos\trip.jpg", migrated.GetImage(7)!.Path);
            Assert.EndsWith(@"\C$\Photos\trip.mp4", new VideoRepository(migrated).Get(18)!.Path);
            Assert.Null(SpaceSourceRouting.Resolve(migrated, "/api/gateway/v1/assets/images/7"));
            using var query = migrated.Command("SELECT COUNT(*) FROM sync_publishers WHERE space_id=$space AND state='legacy-unbound'");
            query.Parameters.AddWithValue("$space", result.SpaceId);
            Assert.Equal(1L, query.ExecuteScalar());
        }
        var repeated = migrator.Migrate(path, "old-owner", Password, "原有照片");
        Assert.Equal(result.SpaceId, repeated.SpaceId);
        Assert.Equal(result.BackupDirectory, repeated.BackupDirectory);
    }

    [Fact]
    public void ActiveWriterLeavesSpaceInvisibleAndRetrySucceedsAfterWriterCloses()
    {
        var path = Path.Combine(_root, "legacy", "picer.db");
        var accounts = new AccountStore(Path.Combine(_root, "accounts"));
        var guard = new RestoreGuard(Path.Combine(_root, "guard"), accounts.ServiceId, true);
        var migrator = new LegacySpaceMigrator(accounts, guard);
        using (var database = new PicerDatabase(path))
        {
            database.AddSource(@"C:\Photos");
            Assert.Throws<IOException>(() => migrator.Migrate(path, "old-owner", Password, "原有照片"));
            var pending = accounts.GetLegacyMigration()!;
            Assert.Equal("preparing", pending.State);
            Assert.Empty(accounts.ListSpaces(pending.AccountId));
        }
        var result = migrator.Migrate(path, "old-owner", Password, "原有照片");
        Assert.Single(accounts.ListSpaces(result.AccountId));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
