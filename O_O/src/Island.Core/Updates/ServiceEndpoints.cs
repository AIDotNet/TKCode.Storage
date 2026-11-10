namespace Island.Core.Updates;

/// <summary>
/// The O_O service (website, update check, plugin catalog; docs/design/08-updates-and-catalog.md). Its address lives
/// here and nowhere else. <c>O_O_SERVICE_URL</c> overrides it for development (e.g. http://localhost:5080).
/// </summary>
public static class ServiceEndpoints
{
    public const string DefaultBase = "https://island.astra-gate.si";

    public static Uri Base
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("O_O_SERVICE_URL");
            return new Uri((string.IsNullOrWhiteSpace(overridden) ? DefaultBase : overridden.Trim()).TrimEnd('/') + "/");
        }
    }

    /// <summary>Plain http is only for a development service on this machine (<c>O_O_SERVICE_URL=http://localhost…</c>).</summary>
    public static bool IsAllowed(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps ||
        (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    public static Uri Version(string platform, string current) =>
        new(Base, $"api/version?platform={Uri.EscapeDataString(platform)}&current={Uri.EscapeDataString(current)}");

    public static Uri Plugins(string platform) =>
        new(Base, $"api/plugins?platform={Uri.EscapeDataString(platform)}");

    /// <summary>The download platform id of this build: <c>macos-arm64</c>, <c>windows-x64</c>, <c>linux-arm64</c>…</summary>
    public static string BuildPlatform
    {
        get
        {
            var os = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux";
            var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
                _ => "x64",
            };
            return $"{os}-{arch}";
        }
    }

    /// <summary>The plugin platform of this machine: <c>macos</c>, <c>windows</c> or <c>linux</c>.</summary>
    public static string OsPlatform => BuildPlatform.Split('-')[0];
}
