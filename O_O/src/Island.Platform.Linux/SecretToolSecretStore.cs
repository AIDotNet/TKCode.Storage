using System.Diagnostics;

namespace Island.Platform.Linux;

/// <summary>
/// The freedesktop Secret Service (GNOME Keyring, KWallet) through <c>secret-tool</c> from libsecret-tools.
/// The secret travels on stdin/stdout only, never in process arguments, which other users can read with <c>ps</c>.
/// A native D-Bus client can replace this later without changing callers.
/// </summary>
public sealed class SecretToolSecretStore : ISecretStore
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public string Name => "Secret Service (secret-tool)";

    public string? Get(string service, string account)
    {
        var (code, output, error) = Run(["lookup", "service", service, "account", account], stdin: null);
        return code switch
        {
            0 => output,
            1 when error.Length == 0 => null, // not found
            _ => throw new SecretStoreException($"secret-tool lookup failed: {error.Trim()}"),
        };
    }

    public void Set(string service, string account, string secret)
    {
        var (code, _, error) = Run(
            ["store", "--label", $"{service} {account}", "service", service, "account", account], stdin: secret);
        if (code != 0)
        {
            throw new SecretStoreException($"secret-tool store failed: {error.Trim()}");
        }
    }

    public bool Delete(string service, string account)
    {
        var existed = Get(service, account) is not null;
        var (code, _, error) = Run(["clear", "service", service, "account", account], stdin: null);
        if (code != 0 && existed)
        {
            throw new SecretStoreException($"secret-tool clear failed: {error.Trim()}");
        }
        return existed;
    }

    private static (int Code, string Output, string Error) Run(string[] args, string? stdin)
    {
        var info = new ProcessStartInfo("secret-tool")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        Process process;
        try
        {
            process = Process.Start(info) ?? throw new SecretStoreException("Cannot start secret-tool.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new SecretStoreException("secret-tool is not installed (package libsecret-tools).");
        }
        using (process)
        {
            if (stdin is not null)
            {
                process.StandardInput.Write(stdin);
            }
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill();
                throw new SecretStoreException("secret-tool did not answer in time (is the keyring locked?).");
            }
            return (process.ExitCode, output.Result, error.Result);
        }
    }
}
