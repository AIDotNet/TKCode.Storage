using System.Collections.Concurrent;

namespace Island.Platform;

/// <summary>
/// The operating system's credential store (macOS Keychain, Windows Credential Manager, Linux Secret Service).
/// Secrets never go to settings files. Calls may block (an OS prompt, a D-Bus round trip): call off the UI thread.
/// </summary>
public interface ISecretStore
{
    /// <summary>For logs and errors, e.g. "macOS Keychain".</summary>
    string Name { get; }

    /// <returns>The secret, or null when there is none.</returns>
    string? Get(string service, string account);

    void Set(string service, string account, string secret);

    /// <returns>Whether a secret existed.</returns>
    bool Delete(string service, string account);

    /// <summary>
    /// Whether a secret exists, without reading it. Stores that can answer from attributes alone (the macOS
    /// Keychain) do, so this never shows an OS access prompt there.
    /// </summary>
    bool Exists(string service, string account) => Get(service, account) is not null;
}

/// <summary>Raised when the OS store refuses an operation.</summary>
public sealed class SecretStoreException(string message) : Exception(message);

/// <summary>Keeps secrets in memory: tests, and platforms without a store (never persisted).</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<(string, string), string> _secrets = new();

    public string Name => "memory";

    public string? Get(string service, string account) => _secrets.GetValueOrDefault((service, account));

    public void Set(string service, string account, string secret) => _secrets[(service, account)] = secret;

    public bool Delete(string service, string account) => _secrets.TryRemove((service, account), out _);
}
