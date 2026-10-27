using Picer.Core.Data;
using Picer.IndexService.Identity;

namespace Picer.Tests;

public sealed class IndexUpgradePreparationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "picer-upgrade-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Prepare_VisitsLegacyAndEverySpace_WithoutCreatingOtherIndexes()
    {
        Create("picer.db");
        Create(Path.Combine("accounts-v2", "spaces", Guid.NewGuid().ToString("N"), "picer.db"));
        Create(Path.Combine("accounts-v2", "spaces", Guid.NewGuid().ToString("N"), "picer.db"));
        Assert.Equal(3, IndexUpgradePreparation.Prepare(_root));
        Assert.False(File.Exists(Path.Combine(_root, "accounts-v2", "control", "control.db")));
    }

    [Fact]
    public void Prepare_RejectsActiveAccountRuntime()
    {
        Create("picer.db");
        using var lease = new AccountRuntimeLease(Path.Combine(_root, "accounts-v2"));
        Assert.Throws<IOException>(() => IndexUpgradePreparation.Prepare(_root));
    }

    [Fact]
    public void Prepare_RejectsPendingMigrationBeforeChangingAnyIndex()
    {
        Create("picer.db");
        File.WriteAllText(Path.Combine(_root, "picer.db.migration.pending.json"), "{}");
        Assert.Throws<InvalidDataException>(() => IndexUpgradePreparation.Prepare(_root));
    }

    private void Create(string relative)
    {
        using var database = new PicerDatabase(Path.Combine(_root, relative));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
