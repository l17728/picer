using Picer.IndexService.Identity;

namespace Picer.Tests;

public sealed class AccountPasswordAdministrationTests
{
    [Fact]
    public void AdministrativeResetRevokesOldCredentialAndRequiresChange()
    {
        var root = Path.Combine(Path.GetTempPath(), "picer-account-reset-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AccountStore(root);
            var account = store.Register("reset-test", "old-password-123", "test-space");
            Assert.True(store.ContainsLogin("reset-test"));
            Assert.False(store.ContainsLogin("missing-test"));

            store.ResetPasswordForAdministration("reset-test", "new-password-456");

            Assert.Null(store.Authenticate("reset-test", "old-password-123"));
            Assert.Equal(account, store.Authenticate("reset-test", "new-password-456"));
            Assert.True(store.RequiresPasswordChange(account));
            Assert.NotNull(store.SecurityEpoch(account));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
