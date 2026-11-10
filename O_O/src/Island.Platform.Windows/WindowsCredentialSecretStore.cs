using System.Runtime.InteropServices;
using System.Text;

namespace Island.Platform.Windows;

/// <summary>Generic credentials in Windows Credential Manager, named "&lt;service&gt;/&lt;account&gt;".</summary>
public sealed unsafe partial class WindowsCredentialSecretStore : ISecretStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    // CRED_MAX_CREDENTIAL_BLOB_SIZE.
    private const int MaxBlob = 5 * 512;

    public string Name => "Windows Credential Manager";

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public char* TargetName;
        public char* Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public byte* CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public char* TargetAlias;
        public char* UserName;
    }

    private static string Target(string service, string account) => $"{service}/{account}";

    public string? Get(string service, string account)
    {
        fixed (char* target = Target(service, account))
        {
            if (!CredReadW(target, CredTypeGeneric, 0, out var credential))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == ErrorNotFound)
                {
                    return null;
                }
                throw new SecretStoreException($"Credential read failed (error {error}).");
            }
            try
            {
                return Encoding.UTF8.GetString(credential->CredentialBlob, (int)credential->CredentialBlobSize);
            }
            finally
            {
                CredFree(credential);
            }
        }
    }

    public void Set(string service, string account, string secret)
    {
        var blob = Encoding.UTF8.GetBytes(secret);
        if (blob.Length > MaxBlob)
        {
            throw new SecretStoreException($"Secrets are limited to {MaxBlob} bytes on Windows.");
        }
        fixed (char* target = Target(service, account))
        fixed (char* user = account)
        fixed (byte* data = blob)
        {
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                UserName = user,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = data,
                Persist = CredPersistLocalMachine,
            };
            if (!CredWriteW(&credential, 0))
            {
                throw new SecretStoreException($"Credential write failed (error {Marshal.GetLastPInvokeError()}).");
            }
        }
    }

    public bool Delete(string service, string account)
    {
        fixed (char* target = Target(service, account))
        {
            if (CredDeleteW(target, CredTypeGeneric, 0))
            {
                return true;
            }
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorNotFound
                ? false
                : throw new SecretStoreException($"Credential delete failed (error {error}).");
        }
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredReadW(char* target, uint type, uint flags, out Credential* credential);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWriteW(Credential* credential, uint flags);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDeleteW(char* target, uint type, uint flags);

    [LibraryImport("advapi32.dll")]
    private static partial void CredFree(void* buffer);
}
