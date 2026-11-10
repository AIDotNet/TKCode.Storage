namespace Island.Core.Updates;

/// <summary>
/// The decisions of the automatic update (docs/design/08-updates-and-catalog.md): where an installer may come from, how
/// big it may be, which signature a new macOS app must carry, and the script that swaps the app once O_O has quit. Pure,
/// so it is tested without downloading or installing anything.
/// </summary>
public static class UpdatePlan
{
    /// <summary>Installers are at most this big.</summary>
    public const long MaxDownloadBytes = 400L * 1024 * 1024;

    /// <summary>The hosts installers come from: GitHub releases and the storage they redirect to.</summary>
    public static readonly IReadOnlySet<string> TrustedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    };

    /// <summary>Whether an installer may be downloaded from <paramref name="uri"/> (plain http only on this machine, for tests).</summary>
    public static bool IsTrustedDownload(Uri uri) =>
        (uri.Scheme == Uri.UriSchemeHttps && TrustedHosts.Contains(uri.Host)) ||
        (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    /// <summary>The installer's file name, from its address (the release asset's name).</summary>
    public static string FileName(Uri uri)
    {
        var name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        return string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ? "O_O-update" : name;
    }

    /// <summary>The <c>.app</c> bundle that holds <paramref name="executable"/> (…/O_O.app/Contents/MacOS/O_O), or null when it is not one.</summary>
    public static string? MacBundle(string executable)
    {
        var bundle = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(executable) ?? "", "..", ".."));
        return bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(Path.GetDirectoryName(executable)) == "MacOS"
            ? bundle
            : null;
    }

    /// <summary>The team in <c>codesign -dv</c>'s output (<c>TeamIdentifier=ABCDE12345</c>), or null for an unsigned or ad hoc app.</summary>
    public static string? TeamIdentifier(string codesignOutput)
    {
        foreach (var line in codesignOutput.Split('\n'))
        {
            var l = line.Trim();
            if (l.StartsWith("TeamIdentifier=", StringComparison.Ordinal) && l["TeamIdentifier=".Length..] is { Length: > 0 } team && team != "not set")
            {
                return team;
            }
        }
        return null;
    }

    /// <summary>
    /// The macOS swap, run by <c>/bin/sh</c> once O_O (<paramref name="pid"/>) has quit: the old app is moved aside, the
    /// new one copied in (the old one comes back if that fails), the download's quarantine flag dropped, and O_O started
    /// again when <paramref name="restart"/>.
    /// </summary>
    public static string MacSwapScript(int pid, string bundle, string staged, string stageDirectory, bool restart) => $$"""
        #!/bin/sh
        while kill -0 {{pid}} 2>/dev/null; do sleep 0.2; done
        BUNDLE={{Quote(bundle)}}
        OLD="$BUNDLE.update-old"
        rm -rf "$OLD"
        if mv "$BUNDLE" "$OLD"; then
          if /usr/bin/ditto {{Quote(staged)}} "$BUNDLE"; then
            rm -rf "$OLD"
          else
            rm -rf "$BUNDLE"
            mv "$OLD" "$BUNDLE"
          fi
        fi
        /usr/bin/xattr -dr com.apple.quarantine "$BUNDLE" 2>/dev/null
        rm -rf {{Quote(stageDirectory)}}
        {{(restart ? "/usr/bin/open \"$BUNDLE\"" : "true")}}
        """;

    /// <summary>
    /// The Windows install, run by PowerShell once O_O (<paramref name="pid"/>) has quit: the per-user installer runs
    /// silently, then O_O starts again when <paramref name="restart"/>.
    /// </summary>
    public static string WindowsInstallScript(int pid, string installer, string executable, bool restart) => $$"""
        Wait-Process -Id {{pid}} -ErrorAction SilentlyContinue
        Start-Process -FilePath {{PsQuote(installer)}} -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CLOSEAPPLICATIONS' -Wait
        Remove-Item -LiteralPath {{PsQuote(installer)}} -ErrorAction SilentlyContinue
        {{(restart ? $"Start-Process -FilePath {PsQuote(executable)}" : "")}}
        """;

    private static string Quote(string s) => "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string PsQuote(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";
}
