using System.Runtime.InteropServices;
using System.Text;

namespace Island.Platform.MacOS;

/// <summary>
/// Generic passwords in the user's login keychain. Uses the C SecKeychain* API: deprecated in favour of SecItem*, but
/// still supported on macOS 14+ and free of CoreFoundation dictionary plumbing. Items created by O_O are readable by
/// O_O without a prompt; anything else may show the system's access prompt, so call off the UI thread.
/// </summary>
public sealed partial class MacKeychainSecretStore : ISecretStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecItemNotFound = -25300;

    public string Name => "macOS Keychain";

    public string? Get(string service, string account)
    {
        var s = Encoding.UTF8.GetBytes(service);
        var a = Encoding.UTF8.GetBytes(account);
        var status = SecKeychainFindGenericPassword(0, (uint)s.Length, s, (uint)a.Length, a, out var length, out var data, out var item);
        if (status == ErrSecItemNotFound)
        {
            return null;
        }
        Check(status, "read");
        try
        {
            return Marshal.PtrToStringUTF8(data, (int)length);
        }
        finally
        {
            SecKeychainItemFreeContent(0, data);
            CFRelease(item);
        }
    }

    /// <summary>Looks the item up without asking for its data, so another app's item causes no access prompt.</summary>
    public bool Exists(string service, string account)
    {
        var s = Encoding.UTF8.GetBytes(service);
        var a = Encoding.UTF8.GetBytes(account);
        var status = SecKeychainFindGenericPasswordAttributesOnly(0, (uint)s.Length, s, (uint)a.Length, a, 0, 0, out var item);
        if (status == ErrSecItemNotFound)
        {
            return false;
        }
        Check(status, "look up");
        CFRelease(item);
        return true;
    }

    public void Set(string service, string account, string secret)
    {
        var s = Encoding.UTF8.GetBytes(service);
        var a = Encoding.UTF8.GetBytes(account);
        var p = Encoding.UTF8.GetBytes(secret);
        var status = SecKeychainFindGenericPassword(0, (uint)s.Length, s, (uint)a.Length, a, out _, out var data, out var item);
        if (status == 0)
        {
            SecKeychainItemFreeContent(0, data);
            try
            {
                Check(SecKeychainItemModifyAttributesAndData(item, 0, (uint)p.Length, p), "update");
            }
            finally
            {
                CFRelease(item);
            }
            return;
        }
        if (status != ErrSecItemNotFound)
        {
            Check(status, "read");
        }
        Check(SecKeychainAddGenericPassword(0, (uint)s.Length, s, (uint)a.Length, a, (uint)p.Length, p, out var added), "add");
        CFRelease(added);
    }

    public bool Delete(string service, string account)
    {
        var s = Encoding.UTF8.GetBytes(service);
        var a = Encoding.UTF8.GetBytes(account);
        var status = SecKeychainFindGenericPassword(0, (uint)s.Length, s, (uint)a.Length, a, out _, out var data, out var item);
        if (status == ErrSecItemNotFound)
        {
            return false;
        }
        Check(status, "read");
        SecKeychainItemFreeContent(0, data);
        try
        {
            Check(SecKeychainItemDelete(item), "delete");
            return true;
        }
        finally
        {
            CFRelease(item);
        }
    }

    private static void Check(int status, string operation)
    {
        if (status != 0)
        {
            throw new SecretStoreException($"Keychain {operation} failed (OSStatus {status}).");
        }
    }

    [LibraryImport(Security)]
    private static partial int SecKeychainFindGenericPassword(
        nint keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account,
        out uint passwordLength, out nint passwordData, out nint item);

    // The same function with null length/data pointers: only the item reference is returned.
    [LibraryImport(Security, EntryPoint = "SecKeychainFindGenericPassword")]
    private static partial int SecKeychainFindGenericPasswordAttributesOnly(
        nint keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account,
        nint passwordLength, nint passwordData, out nint item);

    [LibraryImport(Security)]
    private static partial int SecKeychainAddGenericPassword(
        nint keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account,
        uint passwordLength, byte[] password, out nint item);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemModifyAttributesAndData(nint item, nint attributes, uint length, byte[] data);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemFreeContent(nint attributes, nint data);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemDelete(nint item);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint obj);
}
