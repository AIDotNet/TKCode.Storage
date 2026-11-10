using Island.Platform.Linux;
using Island.Platform.MacOS;
using Island.Platform.Windows;

namespace Island.Platform.Tests;

/// <summary>
/// Runs against the real OS store of the current platform: writes under a unique throwaway service name and always
/// deletes it again. On a platform without the store's prerequisites the test returns early.
/// </summary>
public class SecretStoreTests
{
    private static ISecretStore? StoreForThisOs() =>
        OperatingSystem.IsMacOS() ? new MacKeychainSecretStore()
        : OperatingSystem.IsWindows() ? new WindowsCredentialSecretStore()
        : OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is not null
            ? new SecretToolSecretStore()
            : null;

    [Fact]
    public void Set_get_update_and_delete_against_the_real_store()
    {
        if (StoreForThisOs() is not { } store)
        {
            return;
        }
        var service = $"O_O.tests/{Guid.NewGuid():N}";
        try
        {
            Assert.Null(store.Get(service, "token"));
            store.Set(service, "token", "first 密钥 ✓");
            Assert.Equal("first 密钥 ✓", store.Get(service, "token"));
            store.Set(service, "token", "second");              // update in place
            Assert.Equal("second", store.Get(service, "token"));
            Assert.Null(store.Get(service, "other"));            // scoped by account
            Assert.True(store.Delete(service, "token"));
            Assert.Null(store.Get(service, "token"));
            Assert.False(store.Delete(service, "token"));
        }
        finally
        {
            store.Delete(service, "token");
        }
    }
}
