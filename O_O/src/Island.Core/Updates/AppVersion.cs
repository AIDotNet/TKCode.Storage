using System.Globalization;
using System.Reflection;

namespace Island.Core.Updates;

/// <summary>
/// O_O's version, <c>major.minor.build</c> (e.g. <c>0.1.1000</c>), compared part by part as numbers. Missing parts
/// count as 0; anything after <c>+</c> (commit) or <c>-</c> (suffix) is ignored.
/// </summary>
public readonly record struct AppVersion(int Major, int Minor, int Build) : IComparable<AppVersion>
{
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var core = text.Trim().TrimStart('v', 'V');
        var cut = core.IndexOfAny(['+', '-']);
        if (cut >= 0)
        {
            core = core[..cut];
        }
        var parts = core.Split('.');
        if (parts.Length is 0 or > 3)
        {
            return false;
        }
        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }
        version = new AppVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    public static AppVersion Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"'{text}' is not a version like 0.1.1000.");

    /// <summary>This app's version (from the entry assembly's informational version).</summary>
    public static AppVersion Current
    {
        get
        {
            var text = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return TryParse(text, out var v) ? v : default;
        }
    }

    public int CompareTo(AppVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major)
        : Minor != other.Minor ? Minor.CompareTo(other.Minor)
        : Build.CompareTo(other.Build);

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

    public override string ToString() => $"{Major}.{Minor}.{Build}";
}
